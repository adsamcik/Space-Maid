using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SpaceMaid.App;
using SpaceMaid.Core.Scanning;
using SpaceMaid.Core.Optimization;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class CloudFilesTests
{
    [Fact]
    [Trait("Stage", "System")]
    public async Task HydratedSyncRootCacheIsKeptBeforeNativeMaintenanceCanTraverseIt()
    {
        using var fixture = new ToolCacheFixture();
        string syncRoot = Directory.CreateDirectory(Path.Join(fixture.Path, "registered-sync-root")).FullName;
        string cache = Directory.CreateDirectory(Path.Join(syncRoot, "npm-cache")).FullName;
        Directory.CreateDirectory(Path.Join(cache, "_cacache"));
        string file = Path.Join(syncRoot, "available.bin"); File.WriteAllBytes(file, new byte[4096]);
        using var registration = RegisterSyncedFixture(syncRoot, file);
        Assert.Equal((FileAttributes)0, File.GetAttributes(syncRoot) & (FileAttributes.ReparsePoint | FileAttributes.Offline));
        fixture.ReportedNpmRoot = cache;
        var observed = (await fixture.Service.InspectAsync()).Single(row => row.Kind == ToolCacheKind.Npm);
        Assert.True(observed.CanPrepare);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.PrepareAsync(observed));
        Assert.DoesNotContain(fixture.Commands, command => command.Arguments.Contains("verify"));
        Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
    }

    internal static IDisposable RegisterSyncedFixture(string root, string file)
    {
        var registration = new Registration { Size = (uint)Marshal.SizeOf<Registration>(), Name = "Space Maid owned test provider", Version = "1", ProviderId = Guid.NewGuid() };
        var policies = new Policies { Size = (uint)Marshal.SizeOf<Policies>(), Hydration = 2, Population = 3 };
        Marshal.ThrowExceptionForHR(CfRegisterSyncRoot(root, ref registration, ref policies, 6));
        try
        {
            using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Marshal.ThrowExceptionForHR(CfConvertToPlaceholder(handle, [1], 1, 1, out _, 0));
            return new RegisteredFixture(root);
        }
        catch { Marshal.ThrowExceptionForHR(CfUnregisterSyncRoot(root)); throw; }
    }

    private sealed class RegisteredFixture(string path) : IDisposable
    {
        public void Dispose() => Marshal.ThrowExceptionForHR(CfUnregisterSyncRoot(path));
    }

    [Theory]
    [InlineData(false, true, 0, 1, 4096, true)]
    [InlineData(false, false, 0, 1, 4096, false)]
    [InlineData(false, true, 10, 1, 4096, false)]
    [InlineData(false, true, 0, 3, 4096, false)]
    [InlineData(false, true, 0, 2, 0, false)]
    [InlineData(true, true, 0, 1, 4096, false)]
    public void OnlySyncedNonexcludedLocalFilesAreEligible(bool directory, bool synced, long modified, int pin, long local, bool eligible)
    {
        Assert.Equal(eligible, new CloudFileState("Fixture provider", directory, local, modified, synced, pin).CanRequestOnlineOnly);
    }

    [Fact]
    public async Task OrdinaryBackedUpLookingFoldersHaveNoOnlineOnlyCapability()
    {
        using var fixture = new TestDirectory();
        string folder = Directory.CreateDirectory(Path.Join(fixture.Path, "OneDrive-Proton-Backup")).FullName;
        string file = Path.Join(folder, "photo.jpg"); File.WriteAllBytes(file, new byte[4096]);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        Assert.Null(CloudFiles.Inspect(file));
        Assert.Null(CloudFiles.Inspect(folder));
        Assert.Throws<IOException>(() => CloudFiles.Prepare(scan, scan.FindEntry(file)!.Value, default));
        Assert.Equal(4096, new FileInfo(file).Length);
    }

    [Fact]
    [Trait("Stage", "System")]
    public async Task WindowsPlaceholderRequestsAreRevalidatedAndReportedAsQueued()
    {
        var operations = new FileOperations(() => throw new InvalidOperationException("Cloud operations must not request NTFS metadata."));
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "fixture.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var registration = new Registration { Size = (uint)Marshal.SizeOf<Registration>(), Name = "Space Maid owned test provider", Version = "1", ProviderId = Guid.NewGuid() };
        var policies = new Policies { Size = (uint)Marshal.SizeOf<Policies>(), Hydration = 2, Population = 3 };
        Marshal.ThrowExceptionForHR(CfRegisterSyncRoot(fixture.Path, ref registration, ref policies, 6));
        try
        {
            using (var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Marshal.ThrowExceptionForHR(CfConvertToPlaceholder(handle, [1], 1, 1, out _, 0));
            var state = Assert.IsType<CloudFileState>(await operations.InspectCloudAsync(file));
            Assert.Equal(registration.Name, state.Provider);
            Assert.True(state.CanRequestOnlineOnly);
            var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
            var plan = await operations.PrepareOnlineOnlyAsync(scan, scan.FindEntry(file)!.Value, default);
            Assert.Equal(1, plan.EligibleCount);
            var updates = new List<OnlineOnlyProgress>();
            var result = await operations.MakeOnlineOnlyAsync(plan, new InlineProgress<OnlineOnlyProgress>(updates.Add), default);
            Assert.Equal(1, result.Requested);
            Assert.Equal(0, result.ConfirmedOnlineOnly); // No real provider is connected to complete it.
            Assert.Equal(2, CloudFiles.Inspect(file)!.PinState);
            Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
            Assert.Equal(1, Assert.Single(updates).Processed);
            var folderPlan = await operations.PrepareOnlineOnlyAsync(scan, -1, default);
            Assert.Equal(1, folderPlan.EligibleCount);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.True((await operations.MakeOnlineOnlyAsync(folderPlan, null, cancelled.Token)).Cancelled);
            // A planned identity mismatch must not redirect a confirmed request.
            var wrongIdentity = new OnlineOnlyPlan(file, state.Provider,
                [new(file, state with { Identity = new(ulong.MaxValue) })], 0, []);
            var replaced = await operations.MakeOnlineOnlyAsync(wrongIdentity, null, default);
            Assert.Equal(0, replaced.Requested);
            Assert.Equal(1, replaced.Skipped);
            Assert.Equal(4096, CloudFiles.Inspect(file)!.LocalBytes);
        }
        finally { Marshal.ThrowExceptionForHR(CfUnregisterSyncRoot(fixture.Path)); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Registration
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Version;
        public nint RootIdentity;
        public uint RootIdentityLength;
        public nint FileIdentity;
        public uint FileIdentityLength;
        public Guid ProviderId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Policies
    {
        public uint Size;
        public ushort Hydration, HydrationModifiers, Population, PopulationModifiers;
        public uint InSync, HardLinks, PlaceholderManagement;
    }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfRegisterSyncRoot(string path, ref Registration registration, ref Policies policies, uint flags);
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfUnregisterSyncRoot(string path);
    [DllImport("cldapi.dll")] private static extern int CfConvertToPlaceholder(SafeFileHandle handle, byte[] identity, uint identityLength, uint flags, out long usn, nint overlapped);
}
