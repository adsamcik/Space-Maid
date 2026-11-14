using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using SpaceMaid.App;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.IO;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class RecyclePermissionRepairTests
{
    [Theory]
    [Trait("Stage", "System")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairAddsDeleteAccessOnlyToTheConfirmedItemAndTheFolderCanRecycle(bool repairFolder)
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "folder")).FullName;
        string file = Path.Join(folder, "payload.txt");
        string sibling = Path.Join(folder, "sibling.txt");
        File.WriteAllText(file, "fixture payload");
        File.WriteAllText(sibling, "keep sibling ACL");
        // Keep an independent grant on both children when the folder's
        // inheritance is changed to prepare the fixture.
        PreserveAccess(file);
        PreserveAccess(sibling);
        var files = new DiskAccess();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var target = FolderDeletionTarget.CaptureCurrent(folder, 15);
        var captured = target.ExportCapturedIdentity();
        string itemPath = repairFolder ? folder : file;
        FileSystemInfo item = repairFolder ? new DirectoryInfo(folder) : new FileInfo(file);
        var parent = new DirectoryInfo(Path.GetDirectoryName(itemPath)!);
        var originalParent = parent.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        byte[] originalItem = ReadSecurity(item).GetSecurityDescriptorBinaryForm();
        string? recycled = null;
        try
        {
            // As owner the normal user can open WRITE_DAC and WRITE_OWNER.
            // Omit DELETE on the verified target and DELETE_CHILD on its parent.
            parent.SetAccessControl(Restricted(new DirectorySecurity(), FileSystemRights.Modify));
            WriteSecurity(item, Restricted(repairFolder ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity(),
                FileSystemRights.ReadAndExecute | FileSystemRights.TakeOwnership));
            var untouched = new FileInfo(sibling).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            using (var denied = files.OpenHandle(itemPath, 0x10000, 7, 0, 3, 0x02300000, 0))
                Assert.True(denied.IsInvalid);
            var exact = files.CaptureRecyclePermissionTarget(captured, itemPath);
            var existingRights = ReadSecurity(item).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Single().FileSystemRights;
            files.RepairRecyclePermission(captured, exact, user, default);
            var after = ReadSecurity(item);
            Assert.Equal(user, after.GetOwner(typeof(SecurityIdentifier)));
            var rules = after.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.All(rules, ace =>
            {
                Assert.Equal(user, ace.IdentityReference);
                Assert.Equal(AccessControlType.Allow, ace.AccessControlType);
                Assert.Equal(InheritanceFlags.None, ace.InheritanceFlags);
            });
            Assert.Equal(existingRights | FileSystemRights.Delete, rules.Aggregate((FileSystemRights)0, (rights, ace) => rights | ace.FileSystemRights));
            using (var allowed = files.OpenHandle(itemPath, 0x10000, 7, 0, 3, 0x02300000, 0))
                Assert.False(allowed.IsInvalid);
            Assert.Equal(untouched, new FileInfo(sibling).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
            var moved = await FolderRecycler.RecycleAsync(target, 0, default);
            recycled = Assert.IsType<string>(moved.RecycledPath);
            Assert.Equal("fixture payload", File.ReadAllText(Path.Join(recycled, "payload.txt")));
        }
        finally
        {
            var acl = new DirectorySecurity();
            acl.SetSecurityDescriptorBinaryForm(originalParent, AccessControlSections.Access);
            if (recycled is not null) DeletionTests.RestoreFixture(recycled, folder);
            parent.SetAccessControl(acl);
            var restore = repairFolder ? (FileSystemSecurity)new DirectorySecurity() : new FileSecurity();
            restore.SetSecurityDescriptorBinaryForm(originalItem, AccessControlSections.Access);
            WriteSecurity(item, restore);
        }

        T Restricted<T>(T acl, FileSystemRights rights) where T : FileSystemSecurity
        {
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, rights, AccessControlType.Allow));
            return acl;
        }
    }

    private static void PreserveAccess(string path)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(true, true);
        file.SetAccessControl(acl);
    }

    [Fact]
    public void AReplacedItemAndAnOutsidePathAreRejectedBeforeChangingPermissions()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "folder")).FullName;
        string item = Path.Join(folder, "payload.txt");
        string outside = Path.Join(fixture.Path, "outside.txt");
        File.WriteAllText(item, "original");
        File.WriteAllText(outside, "outside");
        var files = new DiskAccess();
        var captured = FolderDeletionTarget.CaptureCurrent(folder, 8).ExportCapturedIdentity();
        Assert.Throws<IOException>(() => files.CaptureRecyclePermissionTarget(captured, outside));
        var exact = files.CaptureRecyclePermissionTarget(captured, item);
        File.Move(item, item + ".original");
        File.WriteAllText(item, "replacement");
        var acl = ReadSecurity(new FileInfo(item)).GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Throws<IOException>(() => files.RepairRecyclePermission(captured, exact, identity.User!, default));
        Assert.Equal(acl, ReadSecurity(new FileInfo(item)).GetSecurityDescriptorBinaryForm());
        Assert.Equal("original", File.ReadAllText(item + ".original"));
        Assert.Equal("replacement", File.ReadAllText(item));
    }

    private static FileSystemSecurity ReadSecurity(FileSystemInfo item) => item is DirectoryInfo directory
        ? directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
        : ((FileInfo)item).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);

    private static void WriteSecurity(FileSystemInfo item, FileSystemSecurity acl)
    {
        if (item is DirectoryInfo directory) directory.SetAccessControl((DirectorySecurity)acl);
        else ((FileInfo)item).SetAccessControl((FileSecurity)acl);
    }
}
