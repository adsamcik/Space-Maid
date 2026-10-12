using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static SpaceMaid.LocalModel.WindowsSandboxNative;

namespace SpaceMaid.LocalModel;

/// <summary>
/// Owns a single, Windows-enforced less-privileged AppContainer worker. The worker receives
/// the selected model, read-only runtime files, private scratch, explicit IPC handles,
/// and the sole registry-read capability needed for Windows native bootstrap.
/// Launch failure never falls back to an ordinary process.
/// </summary>
public sealed class WindowsModelSandbox : IDisposable, IAsyncDisposable
{
    private KernelHandle? _process, _job;
    private WindowsSandboxFiles? _files;
    private string? _profile;
    private nint _sid;
    private int _disposed;
    private const string RegistryReadCapability = "S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122-3456934681";

    public Stream StandardInput { get; private set; } = Stream.Null;
    public Stream StandardOutput { get; private set; } = Stream.Null;
    public Stream StandardError { get; private set; } = Stream.Null;
    public int ProcessId { get; private set; }
    internal string ScratchDirectory { get; private set; } = "";
    internal string SandboxSid { get; private set; } = "";
    public string IsolationDescription => "Windows LPAC · no network · read-only model/runtime · no child processes";

    private WindowsModelSandbox() { }

    public static WindowsModelSandbox Start(string executablePath, string modelPath, string workingDirectory,
        long memoryLimitBytes = 16L * 1024 * 1024 * 1024) =>
        StartCore(executablePath, modelPath, workingDirectory, memoryLimitBytes, probe: false);

    internal static WindowsModelSandbox StartProbe(string executablePath, string modelPath, string workingDirectory,
        long memoryLimitBytes = 1024L * 1024 * 1024) =>
        StartCore(executablePath, modelPath, workingDirectory, memoryLimitBytes, probe: true);

    private static WindowsModelSandbox StartCore(string executablePath, string modelPath, string workingDirectory,
        long memoryLimitBytes, bool probe)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Local model isolation requires Windows 10 or newer.");
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The isolated local model requires a 64-bit application.");
        if (memoryLimitBytes < 256L * 1024 * 1024 || memoryLimitBytes > 128L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        var executable = WindowsSandboxFiles.LocalPath(executablePath);
        var model = WindowsSandboxFiles.LocalPath(modelPath);
        var runtime = WindowsSandboxFiles.LocalPath(workingDirectory);
        if (!string.Equals(Path.GetDirectoryName(executable), runtime, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "SpaceMaid.ModelWorker.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The bundled model worker must be in its dedicated runtime directory.");
        if (!string.Equals(Path.GetExtension(model), ".gguf", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a local GGUF model outside the worker runtime directory.", nameof(modelPath));

        var owner = new WindowsModelSandbox();
        try
        {
            var profile = "SpaceMaid.Model." + Guid.NewGuid().ToString("N");
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(profile, "Space Maid local model",
                "Isolated local model worker", 0, 0, out owner._sid));
            owner._profile = profile;
            var sid = new SecurityIdentifier(owner._sid);
            owner.SandboxSid = sid.Value;
            Marshal.ThrowExceptionForHR(GetAppContainerFolderPath(sid.Value, out var folderPointer));
            string scratch;
            try { scratch = Marshal.PtrToStringUni(folderPointer) ?? throw new IOException("No isolated scratch directory."); }
            finally { Marshal.FreeCoTaskMem(folderPointer); }
            owner.ScratchDirectory = scratch;
            Directory.CreateDirectory(Path.Combine(scratch, "Temp"));

            owner._files = new WindowsSandboxFiles(sid);
            owner._files.GrantRuntime(runtime);
            owner._files.GrantModel(model);
            owner._job = CreateBoundedJob(memoryLimitBytes);

            using var stdin = Pipe.Create(parentWrites: true);
            using var stdout = Pipe.Create(parentWrites: false);
            using var stderr = Pipe.Create(parentWrites: false);
            using var attributes = new ProcessAttributes(5);
            // The Windows native loader requires selected OS registry reads in LPAC. Grant
            // that single system capability; no network, file-library, device, or COM caps.
            var registryReadSid = new SecurityIdentifier(RegistryReadCapability);
            var registryBytes = new byte[registryReadSid.BinaryLength];
            registryReadSid.GetBinaryForm(registryBytes, 0);
            using var registrySid = new NativeMemory(registryBytes.Length);
            Marshal.Copy(registryBytes, 0, registrySid.Pointer, registryBytes.Length);
            using var registryCapability = NativeMemory.Structure(new SidAndAttributes { Sid = registrySid.Pointer, Attributes = 4 });
            attributes.Set(0x20009, new SecurityCapabilities { AppContainerSid = owner._sid,
                Capabilities = registryCapability.Pointer, Count = 1 });
            attributes.Set(0x2000f, 1u); // ALL_APPLICATION_PACKAGES_OPT_OUT: LPAC.
            attributes.Set(0x2000e, 1u); // CHILD_PROCESS_RESTRICTED, also enforced by the job limit.
            attributes.SetHandles([stdin.Child.DangerousGetHandle(), stdout.Child.DangerousGetHandle(),
                stderr.Child.DangerousGetHandle()]);
            // Do not disable dynamic code: .NET's JIT needs it. Disable injected extension
            // points, strengthen memory/handle checks, and keep the runtime console detached.
            attributes.Set(0x20007, 0x1UL | 0x4UL | 0x1000UL | 0x10000UL | 0x100000UL |
                0x1000000UL | 0x100000000UL);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 | 1,
                    StandardInput = stdin.Child.DangerousGetHandle(),
                    StandardOutput = stdout.Child.DangerousGetHandle(),
                    StandardError = stderr.Child.DangerousGetHandle()
                },
                AttributeList = attributes.List
            };
            var windows = new StringBuilder(32768);
            var windowsLength = GetWindowsDirectoryW(windows, (uint)windows.Capacity);
            if (windowsLength == 0 || windowsLength >= windows.Capacity) throw Error("Cannot locate Windows runtime");
            using var environment = NativeMemory.Unicode(BuildEnvironment(windows.ToString(), runtime, scratch));
            var command = new StringBuilder(Quote(executable) + (probe ? " --sandbox-probe" : "") + " --model " + Quote(model));
            Check(CreateProcessW(executable, command, 0, 0, true,
                0x00080000 | 0x00000400 | 0x00000008 | 0x00000004, // extended, Unicode env, detached, suspended
                environment.Pointer, scratch, ref startup, out var information), "Cannot create the isolated model worker");
            // CREATE_NO_WINDOW fails inside LPAC during Windows console bootstrap on current
            // builds. DETACHED_PROCESS needs no console/window and preserves explicit pipes.
            owner._process = new KernelHandle(information.Process);
            using var thread = new KernelHandle(information.Thread);
            owner.ProcessId = checked((int)information.ProcessId);
            // No untrusted worker instruction executes before both these checks have succeeded.
            Check(AssignProcessToJobObject(owner._job, owner._process), "Cannot contain the model worker in its resource job");
            VerifyToken(owner._process, sid);
            if (ResumeThread(thread) == uint.MaxValue) throw Error("Cannot resume the isolated model worker");
            owner.StandardInput = stdin.TakeParent();
            owner.StandardOutput = stdout.TakeParent();
            owner.StandardError = stderr.TakeParent();
            return owner;
        }
        catch (Exception original)
        {
            try { owner.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Model isolation setup and cleanup failed.", original, cleanup); }
            throw;
        }
    }

    public int ExitCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var process = _process ?? throw new InvalidOperationException("No model worker was started.");
            if (WaitForSingleObject(process, 0) != 0) throw new InvalidOperationException("The model worker is still running.");
            Check(GetExitCodeProcess(process, out var code), "Cannot read model worker exit code");
            return unchecked((int)code);
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var result = WaitForSingleObject(_process ?? throw new InvalidOperationException("No model worker was started."), 0);
            if (result == 0) return;
            if (result != 258) throw Error("Cannot wait for model worker exit");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static KernelHandle CreateBoundedJob(long memoryLimitBytes)
    {
        var job = CreateJobObjectW(0, 0);
        if (job.IsInvalid) { job.Dispose(); throw Error("Cannot create model worker resource limits"); }
        try
        {
            using var limits = NativeMemory.Structure(new JobExtendedLimits
            {
                Basic = new JobBasicLimits { Flags = 0x2000 | 0x8 | 0x100 | 0x200, ActiveProcessLimit = 1 },
                ProcessMemoryLimit = (nuint)memoryLimitBytes, JobMemoryLimit = (nuint)memoryLimitBytes
            });
            Check(SetInformationJobObject(job, 9, limits.Pointer, limits.Size), "Cannot enforce model memory/process limits");
            using var cpu = NativeMemory.Structure(new JobCpuLimits { Flags = 1 | 4, CpuRate = 8000 });
            Check(SetInformationJobObject(job, 15, cpu.Pointer, cpu.Size), "Cannot enforce model CPU limit");
            using var ui = NativeMemory.Structure(0xffu); // No clipboard, global atoms, desktop/display changes or UI handles.
            Check(SetInformationJobObject(job, 4, ui.Pointer, ui.Size), "Cannot enforce model UI restrictions");
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    private static void VerifyToken(KernelHandle process, SecurityIdentifier expectedSid)
    {
        Check(OpenProcessToken(process, 8 | 2, out var token), "Cannot verify model isolation token");
        using (token)
        {
            using var appContainer = TokenInfo(token, 29);
            using var capabilities = TokenInfo(token, 30);
            using var sid = TokenInfo(token, 31);
            Check(DuplicateToken(token, 2, out var impersonation), "Cannot verify model token group restrictions");
            using var duplicate = impersonation;
            var allPackagesSid = new SecurityIdentifier("S-1-15-2-1");
            var sidBytes = new byte[allPackagesSid.BinaryLength];
            allPackagesSid.GetBinaryForm(sidBytes, 0);
            using var allPackages = new NativeMemory(sidBytes.Length);
            Marshal.Copy(sidBytes, 0, allPackages.Pointer, sidBytes.Length);
            Check(CheckTokenMembershipEx(duplicate, allPackages.Pointer, 1, out var matchesAllPackages),
                "Cannot verify model AppContainer opt-out");
            // TokenIsLessPrivilegedAppContainer (46) is listed by the SDK but returns invalid
            // info class on supported Windows builds. LPAC's documented distinction is that
            // ALL_APPLICATION_PACKAGES no longer matches its AppContainer token.
            if (Marshal.ReadInt32(appContainer.Pointer) != 1 || matchesAllPackages ||
                Marshal.ReadInt32(capabilities.Pointer) != 1 ||
                new SecurityIdentifier(Marshal.ReadIntPtr(capabilities.Pointer, 8)).Value != RegistryReadCapability ||
                new SecurityIdentifier(Marshal.ReadIntPtr(sid.Pointer)) != expectedSid)
                throw new IOException("Windows did not apply the required local model isolation.");
        }
    }

    private static NativeMemory TokenInfo(KernelHandle token, int informationClass)
    {
        GetTokenInformation(token, informationClass, 0, 0, out var size);
        if (size == 0) throw Error("Cannot inspect model isolation token");
        var buffer = new NativeMemory(checked((int)size));
        try
        {
            Check(GetTokenInformation(token, informationClass, buffer.Pointer, size, out _), "Cannot inspect model isolation token");
            return buffer;
        }
        catch { buffer.Dispose(); throw; }
    }

    private static string BuildEnvironment(string windows, string runtime, string scratch)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows, ["WINDIR"] = windows, ["PATH"] = runtime,
            ["LOCALAPPDATA"] = scratch, ["USERPROFILE"] = scratch,
            ["TEMP"] = Path.Combine(scratch, "Temp"), ["TMP"] = Path.Combine(scratch, "Temp"),
            ["DOTNET_EnableDiagnostics"] = "0", ["COMPlus_EnableDiagnostics"] = "0",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };
        return string.Join('\0', environment.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    private static string Quote(string value) => '"' + value + '"';

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception>? errors = null;
        void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        // Closing/terminating the job kills its sole worker even if IPC is blocked or it crashes.
        if (_job is not null) Attempt(() => Check(TerminateJobObject(_job, 1), "Cannot terminate isolated model worker"));
        _job?.Dispose(); // KILL_ON_JOB_CLOSE also applies if explicit termination failed.
        if (_process is not null)
        {
            Attempt(() =>
            {
                // A setup failure before job assignment still leaves an owned suspended process.
                // If the job already terminated it, ERROR_ACCESS_DENIED can mean it is exiting.
                if (WaitForSingleObject(_process, 0) == 258 && !TerminateProcess(_process, 1) &&
                    Marshal.GetLastWin32Error() != 5)
                    throw Error("Cannot terminate isolated model worker");
                if (WaitForSingleObject(_process, 5000) != 0)
                    throw new IOException("The isolated model worker did not exit.");
            });
        }
        Attempt(StandardInput.Dispose);
        Attempt(StandardOutput.Dispose);
        Attempt(StandardError.Dispose);
        _process?.Dispose();
        if (_files is not null) Attempt(_files.Dispose);
        if (_profile is not null) Attempt(() => Marshal.ThrowExceptionForHR(DeleteAppContainerProfile(_profile)));
        if (_sid != 0) FreeSid(_sid);
        if (errors is not null) throw new AggregateException("Model sandbox cleanup failed.", errors);
    }

    public ValueTask DisposeAsync() => new(Task.Run(Dispose));

    private sealed class Pipe : IDisposable
    {
        private SafeFileHandle? _parent;
        internal SafeFileHandle Child { get; }
        private readonly FileAccess _access;
        private Pipe(SafeFileHandle parent, SafeFileHandle child, FileAccess access) =>
            (_parent, Child, _access) = (parent, child, access);
        internal static Pipe Create(bool parentWrites)
        {
            var name = "\\\\.\\pipe\\SpaceMaid.Model." + Guid.NewGuid().ToString("N");
            var parent = CreateNamedPipeW(name, (parentWrites ? 2u : 1u) | 0x40000000u,
                8, 1, 65536, 65536, 0, 0); // overlapped parent, remote clients rejected
            if (parent.IsInvalid) { parent.Dispose(); throw Error("Cannot create isolated model IPC"); }
            SafeFileHandle? child = null;
            try
            {
                var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
                child = CreateInheritableFile(name, parentWrites ? 0x80000000u : 0x40000000u, 0,
                    ref attributes, 3, 0, 0);
                if (child.IsInvalid) throw Error("Cannot connect isolated model IPC");
                if (!ConnectNamedPipe(parent, 0) && Marshal.GetLastWin32Error() != 535)
                    throw Error("Cannot connect isolated model IPC");
                return new Pipe(parent, child, parentWrites ? FileAccess.Write : FileAccess.Read);
            }
            catch { child?.Dispose(); parent.Dispose(); throw; }
        }
        internal FileStream TakeParent()
        {
            var handle = _parent ?? throw new InvalidOperationException("IPC parent already transferred.");
            var stream = new FileStream(handle, _access, 4096, isAsync: true);
            _parent = null;
            return stream;
        }
        public void Dispose() { Child.Dispose(); _parent?.Dispose(); }
    }

    private sealed class NativeMemory : IDisposable
    {
        internal nint Pointer { get; private set; }
        internal uint Size { get; }
        internal NativeMemory(int size) { Pointer = Marshal.AllocHGlobal(size); Size = checked((uint)size); }
        internal static NativeMemory Structure<T>(T value) where T : struct
        {
            var memory = new NativeMemory(Marshal.SizeOf<T>());
            Marshal.StructureToPtr(value, memory.Pointer, false);
            return memory;
        }
        internal static NativeMemory Unicode(string value)
        {
            var memory = new NativeMemory(checked((value.Length + 1) * 2));
            Marshal.Copy((value + '\0').ToCharArray(), 0, memory.Pointer, value.Length + 1);
            return memory;
        }
        public void Dispose() { if (Pointer != 0) Marshal.FreeHGlobal(Pointer); Pointer = 0; }
    }

    private sealed class ProcessAttributes : IDisposable
    {
        internal nint List { get; private set; }
        private readonly List<NativeMemory> _values = [];
        internal ProcessAttributes(int count)
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, count, 0, ref size);
            if (size == 0) throw Error("Cannot prepare model isolation attributes");
            List = Marshal.AllocHGlobal(checked((int)size));
            try { Check(InitializeProcThreadAttributeList(List, count, 0, ref size), "Cannot prepare model isolation attributes"); }
            catch { Marshal.FreeHGlobal(List); List = 0; throw; }
        }
        internal void Set<T>(nuint attribute, T value) where T : struct
        {
            var memory = NativeMemory.Structure(value);
            _values.Add(memory);
            Check(UpdateProcThreadAttribute(List, 0, attribute, memory.Pointer, memory.Size, 0, 0), "Cannot apply model isolation attributes");
        }
        internal void SetHandles(nint[] handles)
        {
            var memory = new NativeMemory(handles.Length * IntPtr.Size);
            _values.Add(memory);
            Marshal.Copy(handles, 0, memory.Pointer, handles.Length);
            Check(UpdateProcThreadAttribute(List, 0, 0x20002, memory.Pointer, memory.Size, 0, 0), "Cannot restrict inherited model IPC handles");
        }
        public void Dispose()
        {
            if (List != 0) { DeleteProcThreadAttributeList(List); Marshal.FreeHGlobal(List); List = 0; }
            foreach (var memory in _values) memory.Dispose();
        }
    }
}
