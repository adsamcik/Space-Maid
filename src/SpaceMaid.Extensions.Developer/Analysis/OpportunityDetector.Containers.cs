using SpaceMaid.Core.Analysis;

namespace SpaceMaid.Extensions.Developer.Analysis;

public static partial class OpportunityDetector
{
    private static Rule? ContainerStorage(IOpportunityInventory scan, int directory, Markers direct)
    {
        const string action = "Open Extensions → Containers & WSL. Inspect local Docker objects there; use the WSL & virtual disks guide for disk locations, guest cleanup and compaction prerequisites. Back up first; never delete the disk file.";
        if ((direct & Markers.VirtualDisk) != 0)
        {
            bool docker = (direct & Markers.DockerDisk) != 0 || (direct & Markers.LinuxDisk) != 0 && AncestorNamed(scan, directory, "Docker");
            string title = docker ? "Docker virtual disk storage" : (direct & Markers.LinuxDisk) != 0 ? "Linux virtual disk storage" : "Virtual disk storage";
            return new(title, "Containers & virtual disks",
                "Contains virtual disk files. Filename and folder context suggest ownership; registration, contents and free guest blocks have not been inspected.",
                "Space Maid keeps this folder. Its size can include several disks and other files. Host file length, allocated size and guest capacity differ; none establishes reclaimable space. Deleting guest files may leave host allocation unchanged. Never recycle a backing disk, even when its app is stopped.",
                action, true, "Manage backing disks through Docker or the virtual machine's owner. Back up and verify shutdown/compaction prerequisites; this folder is kept.");
        }
        const Markers store = Markers.DockerContainers | Markers.DockerImages | Markers.DockerVolumes | Markers.DockerLayers;
        if ((direct & store) == store)
            return new("Docker engine data store", "Containers & virtual disks",
                "Contains containers, image, volumes and overlay2 or windowsfilter engine directories.",
                "Space Maid keeps this engine store. Layer folders and volumes are shared, and stopped agent containers may hold uncommitted work. Filesystem metadata cannot identify unused images or caches; use Docker's current engine report.",
                action, true, "Manage containers, images and build cache in Docker; never recycle engine layer folders or volumes.");
        return null;
    }

    private static Markers VirtualDiskMarker(ReadOnlySpan<char> name)
    {
        Markers value = name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase)
            ? Markers.VirtualDisk : 0;
        if (Is(name, "ext4.vhdx")) value |= Markers.LinuxDisk;
        if (Is(name, "docker_data.vhdx") || Is(name, "DockerDesktop.vhdx")) value |= Markers.DockerDisk;
        return value;
    }
    private static Markers ContainerDirectoryMarker(ReadOnlySpan<char> name) => name switch
    {
        _ when Is(name, "containers") => Markers.DockerContainers,
        _ when Is(name, "image") => Markers.DockerImages,
        _ when Is(name, "volumes") => Markers.DockerVolumes,
        _ when Is(name, "overlay2") || Is(name, "windowsfilter") => Markers.DockerLayers,
        _ => 0
    };
}
