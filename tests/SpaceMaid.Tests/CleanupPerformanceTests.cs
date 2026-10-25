using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace SpaceMaid.Tests;

public sealed class CleanupPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LargeSelectionRechecksEveryTargetWithoutExpandingUnselectedFolders()
    {
        using var fixture = new TestDirectory();
        const int projects = 64, filesPerProject = 256;
        var selectedFiles = new List<string>();
        for (int project = 0; project < projects; project++)
        {
            string root = Directory.CreateDirectory(Path.Join(fixture.Path, $"project-{project:D3}")).FullName;
            File.WriteAllText(Path.Join(root, "app.csproj"), "<Project />");
            string bin = Directory.CreateDirectory(Path.Join(root, "bin")).FullName;
            for (int file = 0; file < filesPerProject; file++)
                File.WriteAllBytes(Path.Join(bin, $"output-{file:D3}.dll"), new byte[64]);
            selectedFiles.Add(Path.Join(bin, "output-000.dll"));
        }
        string unrelated = Directory.CreateDirectory(Path.Join(fixture.Path, "unselected")).FullName;
        string unrelatedFile = Path.Join(unrelated, "keep.bin"); File.WriteAllBytes(unrelatedFile, new byte[7]);
        var scan = await new VolumeScanner().ReadDirectoryAsync(fixture.Path, true, default, allStreams: true);
        var selected = DeveloperOpportunityFixture.Find(scan);
        Assert.Equal(projects, selected.Count);
        foreach (string file in selectedFiles) File.WriteAllBytes(file, new byte[100]);
        File.WriteAllBytes(unrelatedFile, new byte[999]);
        int active = 0, peak = 0, reads = 0;
        var gate = new object();
        var timer = Stopwatch.StartNew();
        var validation = await OpportunityReviewValidator.ValidateAsync(scan, selected,
            discover: DeveloperOpportunityFixture.Find, diagnostic: (name, _) =>
            {
                lock (gate)
                {
                    if (name == "opportunity-validation-read-start") { active++; peak = Math.Max(peak, active); reads++; }
                    if (name == "opportunity-validation-read-complete") active--;
                }
            });
        timer.Stop();
        Assert.Equal(projects, validation.ValidatedLocations.Count);
        Assert.Empty(validation.ExcludedPaths); Assert.Equal(0, active);
        Assert.All(validation.ValidatedLocations, location => Assert.Equal(filesPerProject * 64 + 36, location.LogicalBytes));
        Assert.Equal(7, validation.Snapshot.Entry(validation.Snapshot.FindEntry(unrelatedFile)!.Value).LogicalBytes);
        Assert.Equal(projects * 2, reads);
        Assert.InRange(peak, 2, OpportunityReviewValidator.MaximumConcurrentReads);
        output.WriteLine(JsonSerializer.Serialize(new { Projects = projects, Files = projects * filesPerProject,
            ElapsedMs = timer.Elapsed.TotalMilliseconds, Scopes = reads, PeakConcurrentReads = peak }));
    }
}
