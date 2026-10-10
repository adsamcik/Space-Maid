using SpaceMaid.Core.Analysis;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class FileTypeClassifierTests
{
    [Fact]
    public void ImportsTheFullSuppliedCoverageWithoutTreatingEverySuffixAsAKnownFormat()
    {
        Assert.Equal(9263, FileTypeClassifier.ExtensionCount); Assert.Equal(17051, FileTypeClassifier.FormatCount);
        var model = FileTypeClassifier.Infer("weights.SAFETENSORS");
        Assert.Equal(FileType.MachineLearning, model.Type); Assert.Equal(FileFormatStatus.Known, model.Status);
        Assert.Equal("fmt:safetensors", Assert.Single(model.Candidates).Id);
        Assert.Equal("filename", model.Method); Assert.False(model.ContentVerified);
        Assert.True(Assert.IsAssignableFrom<IList<FileFormatInfo>>(model.Candidates).IsReadOnly);
        var hint = FileTypeClassifier.Infer("ignore.gitignore");
        Assert.Equal(FileFormatStatus.Unknown, hint.Status); Assert.Empty(hint.Candidates);
        Assert.NotNull(hint.Note);
    }

    [Theory]
    [InlineData("photo.JPG", ".jpg", "fmt:jpeg")]
    [InlineData("source.ts", ".ts", "fmt:typescript")]
    [InlineData("model.OBJ", ".obj", "fmt:mime:model/obj")]
    [InlineData("data.BIN", ".bin", "fmt:netron:2e5cef7b2d1a5eb66268")]
    [InlineData("sensor.raw", ".raw", "fmt:archiveteam:cfe35c9e498096d4463c")]
    public void CrossCategoryCollisionsRetainAllCandidatesWithoutGuessing(string name, string suffix, string candidate)
    {
        var match = FileTypeClassifier.Infer(name);
        Assert.Equal(name, match.FileName); Assert.Equal(suffix, match.MatchedValue);
        Assert.Equal(FileType.Ambiguous, match.Type); Assert.Equal(FileFormatStatus.Ambiguous, match.Status);
        Assert.Contains(match.Candidates, format => format.Id == candidate);
        Assert.Contains("Ambiguous", FileTypeClassifier.Describe(name));
        Assert.False(match.ContentVerified);
    }

    [Fact]
    public void MissingHintsDoNotMakeASoleCandidateCertainAndSharedCategoriesRemainUseful()
    {
        var psb = FileTypeClassifier.Infer("asset.psb");
        Assert.Equal("fmt:psb", Assert.Single(psb.Candidates).Id);
        Assert.Equal(FileFormatStatus.Ambiguous, psb.Status); Assert.Equal(FileType.Ambiguous, psb.Type);
        Assert.True(psb.HasUnjoinedEvidence); Assert.Contains("unresolved", psb.Note);
        var flac = FileTypeClassifier.Infer("music.flac");
        Assert.Equal(FileFormatStatus.Ambiguous, flac.Status); Assert.Equal(FileType.Audio, flac.CandidateCategory);
        Assert.Equal(2, flac.Candidates.Count);
        Assert.Contains("Candidate category: Audio", FileTypeClassifier.Describe("music.flac"));
    }

    [Theory]
    [InlineData("archive.tar.gz", "fmt:tar", "fmt:gzip", FileType.ArchiveOrInstaller)]
    [InlineData("drawing.SVG.GZ", "fmt:svg", "fmt:gzip", FileType.Image)]
    public void DocumentedCompositionsAreOneInterpretation(string name, string inner, string outer, FileType type)
    {
        var match = FileTypeClassifier.Infer(name);
        Assert.NotNull(match.Composition); Assert.Equal(inner, match.Composition.Inner.Id); Assert.Equal(outer, match.Composition.Outer.Id);
        Assert.Empty(match.Candidates); Assert.Equal(1, match.InterpretationCount);
        Assert.Equal(FileFormatStatus.Known, match.Status); Assert.Equal(type, match.Type);
        Assert.Contains("Composition:", FileTypeClassifier.DescribeDetails(name));
    }

    [Fact]
    public void LongestSuffixAndNamingConventionsBeatOnlyTheFinalSuffix()
    {
        var declaration = FileTypeClassifier.Infer("types.D.MTS");
        Assert.Equal(".d.mts", declaration.MatchedValue); Assert.Equal(FileType.SourceOrConfiguration, declaration.Type);
        Assert.Equal("fmt:typescript", Assert.Single(declaration.Candidates).Id);
        Assert.Contains("declaration convention", declaration.Note);
        var minified = FileTypeClassifier.Infer("app.min.js");
        Assert.Equal(".min.js", minified.MatchedValue); Assert.Contains("filename convention", minified.Note);
        var tarAlias = FileTypeClassifier.Infer("archive.tgz");
        Assert.NotNull(tarAlias.Composition); Assert.NotEmpty(tarAlias.Candidates);
        Assert.Equal(FileFormatStatus.Ambiguous, tarAlias.Status);
        var svgAlias = FileTypeClassifier.Infer("drawing.svgz");
        Assert.NotNull(svgAlias.Composition); Assert.Equal("fmt:svg", svgAlias.Composition.Inner.Id);
        Assert.Equal(FileType.Image, svgAlias.CandidateCategory); Assert.Equal(FileFormatStatus.Ambiguous, svgAlias.Status);
        var arbitrary = FileTypeClassifier.Infer("sample.mystery.gz");
        Assert.Equal(".gz", arbitrary.MatchedValue); Assert.Null(arbitrary.Composition);
    }

    [Theory]
    [InlineData("Dockerfile", FileNameMatchKind.ExactName, "fmt:dockerfile")]
    [InlineData(".gitignore", FileNameMatchKind.ExactName, "fmt:gitignore")]
    [InlineData("Makefile", FileNameMatchKind.ExactName, "fmt:makefile")]
    [InlineData("places.sqlite-wal", FileNameMatchKind.Suffix, "fmt:sqlite_wal")]
    [InlineData("accounts-wal", FileNameMatchKind.AppendedSuffix, "fmt:sqlite_wal")]
    [InlineData("accounts-shm", FileNameMatchKind.AppendedSuffix, "fmt:sqlite_shm")]
    [InlineData("accounts-journal", FileNameMatchKind.AppendedSuffix, "fmt:sqlite_journal")]
    public void BasenameAndSidecarRulesPreserveSpecificIdentities(string name, FileNameMatchKind kind, string id)
    {
        var match = FileTypeClassifier.Infer(@"Z:\nonexistent-folder\" + name);
        Assert.Equal(name, match.FileName); Assert.Equal(kind, match.MatchKind);
        Assert.Equal(id, Assert.Single(match.Candidates).Id);
        Assert.False(match.ContentVerified);
    }

    [Theory]
    [InlineData("photo.jpg.unlisted_extension_987", FileType.Unknown, ".unlisted_extension_987")]
    [InlineData(".unknown-dotfile", FileType.NoExtension, "")]
    [InlineData("README", FileType.NoExtension, "")]
    [InlineData("photo.jpg.", FileType.NoExtension, "")]
    public void UnknownsDoNotGuessEarlierSuffixesOrCorrectNames(string name, FileType type, string suffix)
    {
        var match = FileTypeClassifier.Infer(name);
        Assert.Equal(name, match.FileName); Assert.Equal(type, match.Type); Assert.Equal(suffix, match.MatchedValue);
        Assert.Equal(FileFormatStatus.Unknown, match.Status); Assert.Empty(match.Candidates); Assert.Null(match.Composition);
    }

    [Fact]
    public void CaseEvidenceDoesNotEraseIndependentAlternativesAndAsciiFoldingPreservesUnicode()
    {
        var upper = FileTypeClassifier.Infer("program.C"); var lower = FileTypeClassifier.Infer("program.c");
        Assert.Equal("C++ source", upper.CaseConvention); Assert.Equal("C source", lower.CaseConvention);
        Assert.Equal(upper.Candidates, lower.Candidates); Assert.Equal(FileFormatStatus.Ambiguous, upper.Status);
        Assert.NotEqual(FileTypeClassifier.Infer("project.kt").MatchedValue, FileTypeClassifier.Infer("project.Kt").MatchedValue);
        Assert.Equal(FileFormatStatus.Unknown, FileTypeClassifier.Infer("project.Kt").Status);
        Assert.Equal(".json", FileTypeClassifier.Infer(".hidden.JSON").MatchedValue);
        Assert.Equal(FileFormatStatus.Unknown, FileTypeClassifier.Infer("LICENSE").Status);
        Assert.Equal(FileType.Document, FileTypeClassifier.Classify("LICENSE"));
        Assert.Equal(".pdf", FileTypeClassifier.Infer("LICENSE.PDF").MatchedValue);
    }

    [Fact]
    public void WarmCategoryLookupsDoNotAllocatePerFilename()
    {
        string[] names = ["photo.JPG", "types.d.ts", "archive.tar.gz", "accounts-wal", "source.C", "data.unknown_987", "README"];
        foreach (string name in names) FileTypeClassifier.Classify(name);
        long before = GC.GetAllocatedBytesForCurrentThread(); int checksum = 0;
        for (int i = 0; i < 10000; i++) checksum += (int)FileTypeClassifier.Classify(names[i % names.Length]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(checksum > 0); Assert.Equal(0, allocated);
    }
}
