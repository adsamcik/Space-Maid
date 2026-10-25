using System.IO;
using System.Runtime.InteropServices;

namespace SpaceMaid.Extensions.Agents;

// Windows owns the Recycle Bin and its progress dialog. Run its COM operation
// on a dedicated STA so the WPF dispatcher stays responsive.
internal static class AgentFileRecycler
{
    internal const int CancelledHResult = unchecked((int)0x800704C7);
    internal const int CannotRecycleHResult = unchecked((int)0x80070032);
    private const uint OperationFlags = 0x00080000 | 0x20000000 | 0x00000400 | 0x00100000 | 0x00000010 | 0x00004000;

    public static Task<bool> RecycleAsync(AgentHistoryItem target, nint owner, CancellationToken token,
        Action<string, object>? diagnostic = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Recycle(target, owner, token, diagnostic)); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Space Maid recycling" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool Recycle(AgentHistoryItem target, nint owner, CancellationToken token,
        Action<string, object>? diagnostic)
    {
        token.ThrowIfCancellationRequested();
        Validate(target);
        using var lease = AgentFileTarget.Lease(target);
        IFileOperation? operation = null;
        IShellItem? item = null;
        uint cookie = 0;
        var sink = new RecycleSink(target, token, diagnostic);
        try
        {
            var type = Type.GetTypeFromCLSID(new("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            operation.SetOwnerWindow(owner);
            // Recycle + session undo. Errors stop the operation and are shown
            // by the app. Keep native progress/cancellation and nuke warnings;
            // never silently authorize permanent deletion.
            operation.SetOperationFlags(OperationFlags);
            operation.Advise(sink, out cookie);
            Guid shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(target.Path, 0, ref shellItemId, out item));
            operation.DeleteItem(item, null);
            token.ThrowIfCancellationRequested();
            Validate(target);
            diagnostic?.Invoke("recycle-native-start", new { target.Path, Flags = $"0x{OperationFlags:X8}" });
            int result = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            diagnostic?.Invoke("recycle-native-finish", new
            {
                target.Path, HResult = $"0x{result:X8}", ItemHResult = $"0x{sink.Result:X8}",
                aborted, sink.CallbackCount, sink.RootCompleted, sink.RecycledPath,
                sink.CannotRecycle, sink.ValidationError
            });
            if (sink.CannotRecycle)
                throw new IOException("Windows cannot recycle this file. Check this drive's Recycle Bin settings. Permanent deletion was blocked.");
            if (sink.ValidationError is { } validation) throw new IOException(validation);
            if (token.IsCancellationRequested || IsCancellation(result) || IsCancellation(sink.Result))
                return false;
            ThrowIfFailed(result);
            ThrowIfFailed(sink.Result);
            if (aborted) throw new IOException("Windows stopped recycling before completion. Some contents may already have moved.");
            // A successful HRESULT alone is insufficient: a non-null new item
            // is Windows' evidence that it was recycled rather than destroyed.
            if (!sink.RootCompleted || sink.RecycledPath is null)
                throw new IOException("Windows did not confirm a Recycle Bin destination. Rescan to check the file's current state.");
            AgentFileTarget.ValidateRecycled(target, sink.RecycledPath);
            diagnostic?.Invoke("recycle-verified", new { target.Path, sink.RecycledPath });
            return true;
        }
        finally
        {
            if (operation is not null)
            {
                if (cookie != 0) operation.Unadvise(cookie);
                Marshal.FinalReleaseComObject(operation);
            }
            if (item is not null) Marshal.FinalReleaseComObject(item);
        }
    }

    private static void Validate(AgentHistoryItem item)
    {
        if (AgentFileTarget.Capture(item.Path) != item.Identity) throw new IOException("This history file changed after confirmation. Inspect again.");
    }

    // Testable policy gate as well as a live PreDeleteItem guard. Windows must
    // request recycling; a permanent-delete callback is never accepted.
    internal static int DeletePermission(uint flags, CancellationToken token) => token.IsCancellationRequested
        ? CancelledHResult : (flags & 0x80) == 0 ? CannotRecycleHResult : 0;

    private static bool IsCancellation(int result) => result is CancelledHResult
        or unchecked((int)0x80270000) or unchecked((int)0x80270001);

    private static void ThrowIfFailed(int result)
    {
        if (result >= 0) return;
        // The Shell's copy-engine HRESULTs do not consistently have localized
        // exception messages. Keep the original code in the exception/log.
        string? message = unchecked((uint)result) switch
        {
            0x80070020 or 0x80070021 or 0x80270027 or 0x80270028 => "A file is in use. Close the app using it, then try again.",
            0x80070005 or 0x80270021 or 0x80270022 or 0x80270002 => "Windows denied access. Check file permissions, then try again.",
            0x80270037 => "This file is too large for the Recycle Bin. Increase this drive's Recycle Bin limit, then try again.",
            0x80270036 or 0x8027003A => "The Recycle Bin is unavailable for this file. Check this drive's Recycle Bin settings.",
            0x80270038 or 0x8027001D or 0x8027001E => "This path is too long for Windows recycling. Open it in File Explorer to review it.",
            0x80070070 or 0x80270032 or 0x80270033 => "Windows has insufficient space to complete recycling.",
            _ => null
        };
        if (message is not null) throw new IOException(message, result);
        Marshal.ThrowExceptionForHR(result);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(AgentHistoryItem target, CancellationToken token,
        Action<string, object>? diagnostic) : IFileOperationProgressSink
    {
        public bool CannotRecycle { get; private set; }
        public string? ValidationError { get; private set; }
        public string? RecycledPath { get; private set; }
        public int Result { get; private set; }
        public bool RootCompleted { get; private set; }
        public int CallbackCount { get; private set; }
        public int StartOperations() => token.IsCancellationRequested ? CancelledHResult : 0;
        public int FinishOperations(int result) { if (result < 0) Result = result; return 0; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            CallbackCount++;
            int result = DeletePermission(flags, token);
            CannotRecycle |= result == CannotRecycleHResult;
            if (result != 0)
                diagnostic?.Invoke("recycle-delete-blocked", new { target.Path, Flags = $"0x{flags:X8}", HResult = $"0x{result:X8}" });
            if (result != 0) return result;
            try
            {
                item.GetDisplayName(0x80058000, out string path);
                if (path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) Validate(target);
                else
                    throw new IOException("Windows requested a file outside the confirmed location.");
                return 0;
            }
            catch (Exception ex) { ValidationError = ex.Message; return unchecked((int)0x80004005); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem)
        {
            if (result < 0) Result = result;
            try
            {
                item.GetDisplayName(0x80058000, out string source);
                bool root = source.Equals(target.Path, StringComparison.OrdinalIgnoreCase);
                if (root || result < 0)
                    diagnostic?.Invoke("recycle-delete-result", new { Path = source, Root = root, Flags = $"0x{flags:X8}", HResult = $"0x{result:X8}" });
                if (root)
                {
                    RootCompleted = result >= 0;
                    if (result >= 0 && newItem is not null)
                    {
                        newItem.GetDisplayName(0x80058000, out string path); // SIGDN_FILESYSPATH
                        RecycledPath = path;
                    }
                }
            }
            catch (COMException ex) { Result = ex.HResult; }
            return token.IsCancellationRequested ? CancelledHResult : 0;
        }
        public int UpdateProgress(uint total, uint completed) => token.IsCancellationRequested ? CancelledHResult : 0;
        public int PreRenameItem(uint f, IShellItem i, string n) => 0;
        public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem? j) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? j) => 0;
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int r, IShellItem? j) => 0;
        public int PreNewItem(uint f, IShellItem d, string n) => 0;
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int r, IShellItem? j) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid id, out IShellItem item);

    // Full vtables are required even though this feature only calls delete.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void MoveItems(nint items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void CopyItems(nint items, IShellItem destination);
        void DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        void DeleteItems(nint items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid id, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
