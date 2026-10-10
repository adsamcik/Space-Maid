using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class FileTypeClassifierTests
{
    [Theory]
    [InlineData("photo.JPG", FileType.Image)]
    [InlineData("photo.heic", FileType.Image)]
    [InlineData("photo.jxl", FileType.Image)]
    [InlineData("photo.CR3", FileType.RawPhoto)]
    [InlineData("photo.nef", FileType.RawPhoto)]
    [InlineData("movie.MKV", FileType.Video)]
    [InlineData("recording.flac", FileType.Audio)]
    [InlineData("source.cs", FileType.SourceOrConfiguration)]
    [InlineData("source.ts", FileType.SourceOrConfiguration)]
    [InlineData(".gitignore", FileType.SourceOrConfiguration)]
    [InlineData("project.csproj", FileType.SourceOrConfiguration)]
    [InlineData("report.PDF", FileType.Document)]
    [InlineData("package.tar.gz", FileType.ArchiveOrInstaller)]
    [InlineData("setup.MSIX", FileType.ArchiveOrInstaller)]
    [InlineData("assembly.dll", FileType.Binary)]
    [InlineData("data.sqlite3", FileType.Database)]
    [InlineData("disk.VHDX", FileType.DiskImage)]
    [InlineData("build.log", FileType.LogOrTemporary)]
    [InlineData("photo.jpg.unknown", FileType.Unknown)]
    [InlineData("data.dat", FileType.Unknown)]
    [InlineData("README", FileType.NoExtension)]
    [InlineData("filename.", FileType.NoExtension)]
    public void InfersCommonCategoriesFromTheFinalSuffix(string filename, FileType expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Classify(filename));
    }

    [Theory]
    [InlineData("photo.JPG", "Inferred type: Image (.jpg)")]
    [InlineData("package.tar.gz", "Inferred type: Archive / installer (.gz)")]
    [InlineData("photo.jpg.unknown", "Inferred type: Unknown (.unknown)")]
    [InlineData("README", "Inferred type: No extension")]
    public void DescriptionsIdentifyFilenameInferenceAndUnknownResults(string filename, string expected)
    {
        Assert.Equal(expected, FileTypeClassifier.Describe(filename));
    }
}
