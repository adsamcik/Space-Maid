using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SpaceMaid.Tests;

// The fixture remains readable but moving it requires the elevated
// Administrators grant. The caller owns both directories and their contents.
internal sealed class RecyclePermissionFixture : IDisposable
{
    private readonly DirectoryInfo _folder, _parent;
    private readonly DirectorySecurity _folderAcl, _parentAcl;
    private bool _disposed;

    internal RecyclePermissionFixture(string path)
    {
        _folder = new(path);
        _parent = _folder.Parent!;
        _folderAcl = _folder.GetAccessControl(AccessControlSections.Access);
        _parentAcl = _parent.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        try
        {
            _parent.SetAccessControl(Restricted(FileSystemRights.Modify));
            _folder.SetAccessControl(Restricted(FileSystemRights.ReadAndExecute));
        }
        catch { Dispose(); throw; }

        DirectorySecurity Restricted(FileSystemRights rights)
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            const InheritanceFlags children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, rights, children, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
            return acl;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Restore(_parent, _parentAcl);
        if (_folder.Exists) Restore(_folder, _folderAcl);
        _disposed = true;
    }

    private static void Restore(DirectoryInfo directory, DirectorySecurity acl)
    {
        acl.SetSecurityDescriptorBinaryForm(acl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        directory.SetAccessControl(acl);
    }
}
