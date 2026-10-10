using System.IO;
using System.Text.Json;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class ScanLogTests
{
    [Fact]
    public void ActiveLogIsReadableAndRetentionKeepsTenRuns()
    {
        using var fixture = new TestDirectory();
        string? first = null;
        for (int i = 0; i < 11; i++)
        {
            using var log = new ScanLog(fixture.Path);
            first ??= log.FilePath;
            log.Record("backend-selected", new { Backend = "Fast NTFS scan", Attempt = i });
            Assert.Null(log.Error);
            using var reader = new StreamReader(new FileStream(log.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            using var entry = JsonDocument.Parse(reader.ReadLine()!);
            Assert.Equal("backend-selected", entry.RootElement.GetProperty("Event").GetString());
            Assert.Equal(i, entry.RootElement.GetProperty("Data").GetProperty("Attempt").GetInt32());
        }
        Assert.Equal(10, Directory.GetFiles(fixture.Path, "scan-*.jsonl").Length);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void LogLimitIsExplicitAndFurtherWritesStayBounded()
    {
        using var fixture = new TestDirectory();
        string? path;
        using (var log = new ScanLog(fixture.Path))
        {
            path = log.FilePath;
            log.Record("oversized", new { Message = new string('x', (int)ScanLog.MaximumBytes) });
            log.Record("later-event", new { Message = "This must not grow the capped log." });
        }
        Assert.True(new FileInfo(path!).Length <= ScanLog.MaximumBytes);
        Assert.Contains("log-size-limit", File.ReadAllText(path!));
        Assert.DoesNotContain("later-event", File.ReadAllText(path!));
    }

    [Fact]
    public void UnwritableLogLocationReportsAnErrorWithoutThrowingDuringScanEvents()
    {
        using var fixture = new TestDirectory();
        string file = Path.Join(fixture.Path, "existing-file");
        File.WriteAllText(file, "preserve");
        using var log = new ScanLog(file);
        Assert.NotNull(log.Error);
        log.Record("scan-start", new { Target = @"G:\" });
        Assert.Equal("preserve", File.ReadAllText(file));
    }
}
