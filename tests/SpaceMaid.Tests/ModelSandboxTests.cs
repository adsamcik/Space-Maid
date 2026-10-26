using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using SpaceMaid.LocalModel;
using Xunit;

namespace SpaceMaid.Tests;

[Collection("Model sandbox")]
public sealed class ModelSandboxTests
{
    [Theory]
    [InlineData("SpaceMaid.ModelWorker.exe", "C:\\model.gguf", "C:\\worker")]
    [InlineData("\\\\server\\worker\\SpaceMaid.ModelWorker.exe", "C:\\model.gguf", "C:\\worker")]
    [InlineData("C:\\worker\\another.exe", "C:\\model.gguf", "C:\\worker")]
    [InlineData("C:\\worker\\SpaceMaid.ModelWorker.exe", "C:\\worker\\model.gguf", "C:\\worker")]
    [InlineData("C:\\worker\\SpaceMaid.ModelWorker.exe", "C:\\model.gguf:stream", "C:\\worker")]
    public void UnsupportedLaunchScopeIsRejectedBeforeProcessCreation(string executable, string model, string runtime)
    {
        Assert.Throws<ArgumentException>(() => WindowsModelSandbox.Start(executable, model, runtime));
    }

    [PublishedModelWorkerFact]
    public async Task WorkerOnlyReadsItsGrantedModelAndRuntime()
    {
        using var fixture = new SandboxFixture();
        var original = Path.Combine(fixture.Directory, "original.bin");
        File.WriteAllText(original, "Original fixture must stay unchanged.");
        var sentinel = Environment.GetEnvironmentVariable("SPACEMAID_SANDBOX_TEST_SECRET");
        Environment.SetEnvironmentVariable("SPACEMAID_SANDBOX_TEST_SECRET", "Not inherited by the model worker.");
        var registryFixture = "Software\\SpaceMaid.SandboxTest." + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var originalUserRegistryKey = identity.User!.Value + "\\" + registryFixture;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using (var ownedKey = Registry.CurrentUser.CreateSubKey(registryFixture))
                ownedKey.SetValue("Sentinel", "Private registry fixture.", RegistryValueKind.String);
            using (var originalKey = Registry.Users.OpenSubKey(originalUserRegistryKey))
                Assert.Equal("Private registry fixture.", originalKey!.GetValue("Sentinel"));
            await using var sandbox = fixture.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runtime = Path.GetDirectoryName(PublishedModelWorkerFactAttribute.WorkerPath!)!;
            var request = new ModelRequest(1, 1, "probe", Probe: new(fixture.Model, runtime, original, original,
                ((IPEndPoint)listener.LocalEndpoint).Port, originalUserRegistryKey));
            await ModelProtocol.WriteAsync(sandbox.StandardInput,
                ModelProtocol.Serialize(request, ModelProtocol.MaximumRequestBytes), deadline.Token);
            var bytes = await new ModelFrameReader(sandbox.StandardOutput, ModelProtocol.MaximumResponseBytes).ReadAsync(deadline.Token);
            Assert.NotNull(bytes);
            var reply = ModelProtocol.Deserialize<ModelReply>(bytes);
            Assert.Null(reply.Error);
            Assert.NotNull(reply.Probe);
            Assert.True(reply.Probe.AppContainer);
            Assert.True(reply.Probe.LowPrivilege);
            Assert.True(reply.Probe.ModelReadAllowed);
            Assert.True(reply.Probe.ModelWriteBlocked);
            Assert.True(reply.Probe.RuntimeWriteBlocked);
            Assert.True(reply.Probe.OutsideReadBlocked);
            Assert.True(reply.Probe.OutsideWriteBlocked);
            Assert.True(reply.Probe.ChildProcessBlocked);
            Assert.True(reply.Probe.EnvironmentSecretAbsent);
            Assert.True(reply.Probe.PrivateRegistryReadBlocked);
            Assert.True(reply.Probe.LoopbackBlocked,
                "The worker must be denied an actual socket connection: " + reply.Probe.NetworkObservation);
            Assert.False(listener.Pending());
            sandbox.StandardInput.Dispose();
            await sandbox.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, sandbox.ExitCode);
            Assert.Equal("Original fixture must stay unchanged.", File.ReadAllText(original));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(registryFixture, throwOnMissingSubKey: false);
            Environment.SetEnvironmentVariable("SPACEMAID_SANDBOX_TEST_SECRET", sentinel);
        }
    }

    [PublishedModelWorkerFact]
    public async Task NativeBackendLoadsAndRejectsAMalformedModelInsideTheSandbox()
    {
        using var fixture = new SandboxFixture();
        var worker = PublishedModelWorkerFactAttribute.WorkerPath!;
        await using var sandbox = WindowsModelSandbox.Start(worker, fixture.Model, Path.GetDirectoryName(worker)!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var diagnostics = new StreamReader(sandbox.StandardError).ReadToEndAsync(deadline.Token);
        var request = new ModelRequest(ModelProtocol.Version, 1, "chat",
            Messages: [new("user", "Inspect this owned test fixture.")], MaxOutputTokens: 16, Temperature: 0);
        await ModelProtocol.WriteAsync(sandbox.StandardInput,
            ModelProtocol.Serialize(request, ModelProtocol.MaximumRequestBytes), deadline.Token);
        var bytes = await new ModelFrameReader(sandbox.StandardOutput, ModelProtocol.MaximumResponseBytes).ReadAsync(deadline.Token);
        Assert.NotNull(bytes);
        var reply = ModelProtocol.Deserialize<ModelReply>(bytes);
        Assert.Equal("model_load_failed", reply.Error);
        Assert.Null(reply.Text);
        sandbox.StandardInput.Dispose();
        await sandbox.WaitForExitAsync(deadline.Token);
        Assert.Equal(13, sandbox.ExitCode);
        Assert.Contains("worker_model_load_failed_LoadWeightsFailedException", await diagnostics);
        Assert.Equal("Model fixture.", File.ReadAllText(fixture.Model));
    }

    [PublishedModelWorkerFact]
    public async Task DisposalKillsIdleWorkerAndRemovesOnlyItsOwnPermissionsAndProfile()
    {
        using var fixture = new SandboxFixture();
        var sandbox = fixture.Start();
        string scratch = sandbox.ScratchDirectory;
        string sid = sandbox.SandboxSid;
        using var process = Process.GetProcessById(sandbox.ProcessId);
        Assert.False(process.HasExited);
        Assert.Contains(AccessRules(fixture.Model), rule => rule.IdentityReference.Value == sid);
        var guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var extra = new FileSystemAccessRule(guests, FileSystemRights.ReadExtendedAttributes, AccessControlType.Allow);
        var security = new FileInfo(fixture.Model).GetAccessControl();
        security.AddAccessRule(extra);
        new FileInfo(fixture.Model).SetAccessControl(security);
        try { await sandbox.DisposeAsync(); }
        finally { sandbox.Dispose(); }
        Assert.True(process.HasExited);
        Assert.DoesNotContain(AccessRules(fixture.Model), rule => rule.IdentityReference.Value == sid);
        Assert.Contains(AccessRules(fixture.Model), rule => rule.IdentityReference.Value == guests.Value &&
            (rule.FileSystemRights & FileSystemRights.ReadExtendedAttributes) != 0);
        Assert.False(System.IO.Directory.Exists(scratch));
        using var write = File.Open(fixture.Model, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [PublishedModelWorkerFact]
    public void FailedModelGrantRestoresRuntimePermissions()
    {
        using var fixture = new SandboxFixture();
        var worker = PublishedModelWorkerFactAttribute.WorkerPath!;
        string before = StableRuntimePermissions(worker);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowsModelSandbox.StartProbe(worker,
            Path.Combine(fixture.Directory, "missing.gguf"), Path.GetDirectoryName(worker)!));
        Assert.Equal(before, StableRuntimePermissions(worker));
    }

    [PublishedModelWorkerFact]
    public void RedirectedModelIsRejectedAndRuntimeGrantsAreRolledBack()
    {
        using var fixture = new SandboxFixture();
        // A directory junction exercises redirected ancestors without needing the Windows
        // symbolic-link privilege. Both the link and target belong to this fixture.
        var junction = Path.Combine(fixture.Directory, "redirected");
        using var create = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{fixture.Directory}\""
        })!;
        create.WaitForExit();
        Assert.Equal(0, create.ExitCode);
        var redirected = Path.Combine(junction, "probe.gguf");
        var worker = PublishedModelWorkerFactAttribute.WorkerPath!;
        string before = StableRuntimePermissions(worker);
        try
        {
            Assert.Throws<IOException>(() => WindowsModelSandbox.StartProbe(worker, redirected, Path.GetDirectoryName(worker)!));
            Assert.Equal(before, StableRuntimePermissions(worker));
        }
        finally { System.IO.Directory.Delete(junction); }
    }

    private static IEnumerable<FileSystemAccessRule> AccessRules(string path) =>
        new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();

    private static string StableRuntimePermissions(string path)
    {
        var descriptor = new RawSecurityDescriptor(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0);
        var acl = descriptor.DiscretionaryAcl!;
        // Other simultaneous model sessions legitimately acquire/release their own package
        // SID grants. Compare the permissions this setup must preserve independently of them.
        for (int i = acl.Count - 1; i >= 0; i--)
            if (acl[i] is KnownAce ace && ace.SecurityIdentifier.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal))
                acl.RemoveAce(i);
        return descriptor.GetSddlForm(AccessControlSections.Access);
    }

    private sealed class SandboxFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "SpaceMaid.ModelSandbox." + Guid.NewGuid().ToString("N"));
        internal string Model => Path.Combine(Directory, "probe.gguf");
        internal SandboxFixture() { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Model, "Model fixture."); }
        internal WindowsModelSandbox Start()
        {
            var worker = PublishedModelWorkerFactAttribute.WorkerPath!;
            return WindowsModelSandbox.StartProbe(worker, Model, Path.GetDirectoryName(worker)!);
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Directory);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("SpaceMaid.ModelSandbox.", StringComparison.Ordinal))
                throw new IOException("Unexpected sandbox fixture directory.");
            System.IO.Directory.Delete(full, recursive: true);
        }
    }
}

[CollectionDefinition("Model sandbox", DisableParallelization = true)]
public sealed class ModelSandboxCollection;

/// <summary>Native proof needs the actual dedicated self-contained worker, not a substitute process.</summary>
public sealed class PublishedModelWorkerFactAttribute : FactAttribute
{
    internal static string? WorkerPath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SPACEMAID_TEST_MODEL_WORKER");
            if (configured is not null && File.Exists(configured)) return Path.GetFullPath(configured);
            var packaged = Path.Combine(AppContext.BaseDirectory, "Extensions", "space-maid.assistant", "Worker", "SpaceMaid.ModelWorker.exe");
            if (File.Exists(packaged)) return packaged;
            var parent = new DirectoryInfo(AppContext.BaseDirectory);
            while (parent is not null)
            {
                foreach (var child in new[] { "ModelWorker", Path.Combine("artifacts", "model-worker-publish") })
                {
                    var candidate = Path.Combine(parent.FullName, child, "SpaceMaid.ModelWorker.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                parent = parent.Parent;
            }
            return null;
        }
    }
    public PublishedModelWorkerFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-enforced process isolation requires Windows.";
        else if (WorkerPath is null) Skip = "Publish the self-contained ModelWorker or set SPACEMAID_TEST_MODEL_WORKER for native isolation proof.";
    }
}
