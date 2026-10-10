using System.Diagnostics;
using System.Globalization;
using System.IO;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;
using Xunit;

namespace SpaceMaid.Tests;

public sealed class GitWorktreeAnalyzerTests
{
    [Fact]
    public async Task OldCleanRegisteredIntegratedWorktreeQualifiesWithoutRunningFsmonitorOrWritingItsIndex()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string sentinel = Path.Join(fixture.Path, "must-not-run.txt"), hook = Path.Join(fixture.Path, "fsmonitor.sh");
        File.WriteAllText(hook, "#!/bin/sh\nprintf unsafe > '" + sentinel.Replace('\\', '/') + "'\n");
        await GitFixture.Run(repo.Main, repo.Old, "config", "core.fsmonitor", hook);
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        string index = Path.Join(gitDir, "index"); byte[] before = File.ReadAllBytes(index);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var result = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.True(result.Recommended, result.Status + ": " + result.Reason);
        Assert.True(result.VerifiedClean); Assert.True(result.Integrated); Assert.True(result.HasActivityEvidence);
        Assert.True(WorktreeCleanupPolicy.Default.Matches(result, DateTime.UtcNow));
        Assert.Equal("Stale worktree", result.Status); Assert.Equal("old", result.Branch);
        Assert.Equal("refs/remotes/origin/main", result.IntegrationTarget);
        Assert.True(result.LastObservedModificationUtc <= repo.Old.AddSeconds(2));
        Assert.True(result.LastReflogActivityUtc <= repo.Old.AddSeconds(2));
        Assert.Contains("actual use are unknown", result.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel)); Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal("owned tracked source", File.ReadAllText(repo.Source));
        var flags = (byte[])scan.EntryFlags.Clone(); flags[scan.RootIndex] |= 2;
        var unrelatedWarning = new ScanSnapshot(scan.RootPath, scan.Backend, scan.Records, scan.Links, scan.NameArena,
            scan.RootIndex, scan.FirstLinks, scan.ChildHeads, scan.NextLinks, scan.DirectoryLogical, scan.DirectoryAllocated,
            scan.IncludesAllDataStreams, scan.HasAllocationData, scan.HasCompleteVolumeLinks, ["An unrelated branch could not be read."],
            scan.Metrics, flags, 1, scan.DirectoryCount, scan.FallbackReason);
        Assert.True((await GitWorktreeAnalyzer.AssessAsync(unrelatedWarning, Finding(unrelatedWarning, repo.Checkout))).Recommended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackedChangesAndUntrackedFilesAreProtectedEvenWhenTheirModificationTimesAreOld(bool untracked)
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string changed = untracked ? Path.Join(repo.Checkout, "untracked.txt") : repo.Source;
        File.WriteAllText(changed, "local work that must be preserved"); GitFixture.Age(repo.Checkout, repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var result = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(result.Recommended); Assert.Equal("Local changes", result.Status);
        Assert.False(result.VerifiedClean);
        foreach (var mode in Enum.GetValues<WorktreeCleanupMode>())
            Assert.False(new WorktreeCleanupPolicy(mode, 1).Matches(result, DateTime.UtcNow));
        Assert.Equal("local work that must be preserved", File.ReadAllText(changed));
    }

    [Fact]
    public async Task UnintegratedCommitsLockedRegistrationsAndUnsafeFiltersRequireReview()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        File.AppendAllText(repo.Source, "\nunique commit");
        await GitFixture.Run(repo.Checkout, repo.Old, "add", "--", "source.txt");
        await GitFixture.Run(repo.Checkout, repo.Old, "commit", "-m", "owned unique commit");
        GitFixture.Age(repo.Checkout, repo.Old);
        await GitFixture.AgeReflogs(repo.Checkout, repo.Main, "old", repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var unmatched = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(unmatched.Recommended); Assert.Equal("Unmerged commits", unmatched.Status);
        Assert.True(unmatched.VerifiedClean); Assert.False(unmatched.Integrated); Assert.True(unmatched.HasActivityEvidence);
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old).Matches(unmatched, DateTime.UtcNow));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Integrated).Matches(unmatched, DateTime.UtcNow));
        await GitFixture.Run(repo.Main, repo.Old, "worktree", "lock", repo.Checkout);
        var locked = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(locked.Recommended); Assert.Contains("locked", locked.Reason, StringComparison.Ordinal);
        await GitFixture.Run(repo.Main, repo.Old, "worktree", "unlock", repo.Checkout);
        string sentinel = Path.Join(fixture.Path, "filter-must-not-run.txt");
        await GitFixture.Run(repo.Main, repo.Old, "config", "filter.fixture.clean", "cmd /c echo unsafe > " + sentinel);
        var filter = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(filter.Recommended); Assert.Contains("content filters", filter.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel)); Assert.Contains("unique commit", File.ReadAllText(repo.Source), StringComparison.Ordinal);
        await GitFixture.Run(repo.Main, repo.Old, "config", "--unset", "filter.fixture.clean");
        await GitFixture.Run(repo.Main, repo.Old, "config", "extensions.worktreeConfig", "true");
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        File.WriteAllText(Path.Join(gitDir, "config.worktree"), "[filter unsafe]\nclean = cmd /c echo unsafe > " + sentinel.Replace('\\', '/') + "\n");
        var perWorktree = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(perWorktree.Recommended); Assert.Contains("Worktree-specific Git configuration", perWorktree.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(sentinel));
        File.Delete(Path.Join(repo.Main, ".git", "config"));
        var missingConfig = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(missingConfig.Recommended); Assert.Equal("Needs review", missingConfig.Status);
    }

    [Fact]
    public async Task ReflogRecencyAndMissingActivityEvidenceDoNotEstablishAbandonment()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        await GitFixture.Run(repo.Checkout, DateTime.UtcNow, "switch", "--detach");
        await GitFixture.Run(repo.Checkout, DateTime.UtcNow, "switch", "old");
        GitFixture.Age(repo.Checkout, repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var recent = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(recent.Recommended); Assert.Equal("Recent activity", recent.Status);
        Assert.True(recent.VerifiedClean); Assert.True(recent.Integrated); Assert.True(recent.HasActivityEvidence);
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Integrated).Matches(recent, DateTime.UtcNow));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 1).Matches(recent, DateTime.UtcNow));
        var location = Finding(scan, repo.Checkout);
        var integratedPolicy = new Dictionary<string, WorktreeCleanupPolicy>(StringComparer.Ordinal)
            { [repo.Checkout] = new(WorktreeCleanupMode.Integrated) };
        Assert.Empty(await GitWorktreeGuard.CheckAsync(scan, [location], integratedPolicy));
        var agePolicy = new Dictionary<string, WorktreeCleanupPolicy>(StringComparer.Ordinal)
            { [repo.Checkout] = new(WorktreeCleanupMode.Old, 1) };
        var failedAge = await GitWorktreeGuard.CheckAsync(scan, [location], agePolicy);
        Assert.Contains("captured cleanup condition", failedAge[repo.Checkout], StringComparison.Ordinal);
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        File.Delete(Path.Join(gitDir, "logs", "HEAD"));
        var unavailable = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(unavailable.Recommended); Assert.Contains("reflog activity evidence is unavailable", unavailable.Reason, StringComparison.Ordinal);
        Assert.True(unavailable.VerifiedClean); Assert.True(unavailable.Integrated); Assert.False(unavailable.HasActivityEvidence);
        Assert.Null(unavailable.LastActivityUtc);
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Integrated).Matches(unavailable, DateTime.UtcNow));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old).Matches(unavailable, DateTime.UtcNow));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), new(true)));
    }

    [Fact]
    public async Task ChangedRecommendedWorktreeBlocksBothItsOwnSelectionAndASelectedAncestor()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        Assert.True((await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout))).Recommended);
        string local = Path.Join(repo.Checkout, "new-work.txt"); File.WriteAllText(local, "preserve fresh work");
        var fresh = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var selected = new[] { Finding(fresh, repo.Checkout), Finding(fresh, repo.Parent) };
        var blocked = await GitWorktreeGuard.CheckAsync(fresh, selected, [repo.Checkout]);
        Assert.Contains(repo.Checkout, blocked.Keys); Assert.Contains(repo.Parent, blocked.Keys);
        var plan = CleanupPlanner.Prepare(fresh, selected, blockedPaths: blocked);
        Assert.False(plan.CanExecute); Assert.Empty(plan.Targets); Assert.Equal(2, plan.BlockedLocations.Count);
        Assert.Equal("preserve fresh work", File.ReadAllText(local));
        // A stale cached scan is also refused using current source metadata.
        var stale = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(stale.Recommended); Assert.Equal("Local changes", stale.Status); Assert.False(stale.VerifiedClean);
    }

    [Fact]
    public async Task ReadLeasesBlockSourceAndGitMetadataWritesAndReleaseThemAfterInspection()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string config = Path.Join(repo.Main, ".git", "config"), renamed = repo.Checkout + "-renamed";
        using (var guard = new GitWorktreeAnalyzer.MetadataGuard(default))
        {
            guard.Ancestors(repo.Checkout); guard.Tree(repo.Checkout, rejectNestedGit: true);
            guard.Ancestors(Path.GetDirectoryName(config)!); guard.File(config);
            Assert.Throws<IOException>(() => File.AppendAllText(repo.Source, "unexpected write"));
            Assert.Throws<IOException>(() => File.AppendAllText(config, "\n[filter unsafe]\nclean=cmd /c unsafe\n"));
            Assert.Throws<IOException>(() => Directory.Move(repo.Checkout, renamed));
            Assert.Equal("owned tracked source", File.ReadAllText(repo.Source));
        }
        File.AppendAllText(repo.Source, "\nwrite after release");
        Assert.Contains("write after release", File.ReadAllText(repo.Source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AncestorCoveringSharedGitStorageIsBlockedWhileDirectCheckoutRemainsEligible()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        File.WriteAllText(Path.Join(fixture.Path, "pyvenv.cfg"), "owned environment fixture");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var checkout = Finding(scan, repo.Checkout); var ancestor = Finding(scan, fixture.Path);
        var assessment = await GitWorktreeAnalyzer.AssessAsync(scan, checkout);
        Assert.True(assessment.Recommended, assessment.Reason);
        Assert.Equal(Path.Join(repo.Main, ".git"), assessment.CommonGitDirectory);
        var blocked = await GitWorktreeGuard.CheckAsync(scan, [checkout, ancestor], [repo.Checkout]);
        Assert.Contains(ancestor.Path, blocked.Keys); Assert.DoesNotContain(checkout.Path, blocked.Keys);
        Assert.Contains("shared Git storage", blocked[ancestor.Path], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(repo.Main, ".git", "config")));
    }

    [Fact]
    public async Task DifferentlyCasedRegisteredWorktreeRootRequiresReview()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string variant = Path.Join(repo.Parent, "CHECKOUT");
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        File.WriteAllText(Path.Join(gitDir, "gitdir"), Path.Join(variant, ".git").Replace('\\', '/') + "\n");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var assessment = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(assessment.Recommended); Assert.Equal("Needs review", assessment.Status);
        Assert.Contains("registration does not point back to this exact folder", assessment.Reason, StringComparison.Ordinal);
        Assert.Equal("owned tracked source", File.ReadAllText(repo.Source));
    }

    [Fact]
    public async Task OldCleanCheckoutWithoutAnIntegrationReferenceStillHasVerifiedAgeFacts()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        await GitFixture.Run(repo.Main, repo.Old, "symbolic-ref", "--delete", "refs/remotes/origin/HEAD");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var location = Finding(scan, repo.Checkout);
        var result = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.True(result.VerifiedClean, result.Reason); Assert.False(result.Integrated); Assert.True(result.HasActivityEvidence);
        Assert.Equal("No integration evidence", result.Status); Assert.Null(result.IntegrationTarget);
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 40).Matches(result, DateTime.UtcNow));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 60).Matches(result, DateTime.UtcNow));
        var policies = new Dictionary<string, WorktreeCleanupPolicy>(StringComparer.Ordinal) { [repo.Checkout] = new(WorktreeCleanupMode.Old, 40) };
        Assert.Empty(await GitWorktreeGuard.CheckAsync(scan, [location], policies));
        policies[repo.Checkout] = new(WorktreeCleanupMode.Old, 60);
        Assert.Contains(repo.Checkout, (await GitWorktreeGuard.CheckAsync(scan, [location], policies)).Keys);
        Assert.Equal("owned tracked source", File.ReadAllText(repo.Source));
        Assert.True(File.Exists(Path.Join(repo.Main, ".git", "refs", "heads", "old")));
    }

    [Fact]
    public void CachedPolicyUsesLatestObservedActivityAndAnInclusiveValidatedCutoff()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var result = new GitWorktreeAssessment("owned fixture", false, "Clean", "fixture",
            LastObservedModificationUtc: now.AddDays(-60), LastReflogActivityUtc: now.AddDays(-45),
            VerifiedClean: true, Integrated: true, HasActivityEvidence: true);
        Assert.Equal(now.AddDays(-45), result.LastActivityUtc);
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 45).Matches(result, now));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.OldAndIntegrated, 46).Matches(result, now));
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 45).Matches(result with { LastReflogActivityUtc = now.AddDays(-45).AddTicks(1) }, now));
        Assert.True(new WorktreeCleanupPolicy(WorktreeCleanupMode.Integrated, 365).Matches(result, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorktreeCleanupPolicy(InactiveDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorktreeCleanupPolicy(InactiveDays: 366));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorktreeCleanupPolicy((WorktreeCleanupMode)99));
    }

    private static Opportunity Finding(ScanSnapshot scan, string path) => Assert.Single(OpportunityDetector.Find(scan), finding => finding.Path == path);

    private sealed record GitFixture(string Main, string Parent, string Checkout, string Source, DateTime Old) : IDisposable
    {
        public void Dispose()
        {
            string owned = Path.GetFullPath(Path.Join(Path.GetTempPath(), "SpaceMaid-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Main).StartsWith(owned, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Git fixture cleanup escaped its owned root.");
            // Git creates read-only loose objects. Clear that flag only inside
            // this owned repository so TestDirectory can remove its fixture.
            foreach (string file in Directory.EnumerateFiles(Main, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        public static async Task<GitFixture> Create(string root)
        {
            DateTime old = DateTime.UtcNow.AddDays(-45);
            string main = Directory.CreateDirectory(Path.Join(root, "repository")).FullName;
            await Run(main, old, "init", "--initial-branch=main");
            File.WriteAllText(Path.Join(main, "source.txt"), "owned tracked source");
            await Run(main, old, "add", "--", "source.txt"); await Run(main, old, "commit", "-m", "owned fixture commit");
            await Run(main, old, "update-ref", "refs/remotes/origin/main", "HEAD");
            await Run(main, old, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
            string parent = Directory.CreateDirectory(Path.Join(root, ".venv")).FullName;
            File.WriteAllText(Path.Join(parent, "pyvenv.cfg"), "owned environment fixture");
            string checkout = Path.Join(parent, "checkout"); await Run(main, old, "worktree", "add", "-b", "old", checkout, "main");
            Age(checkout, old);
            string gitDir = (await Run(checkout, old, "rev-parse", "--absolute-git-dir")).Trim();
            File.SetLastWriteTimeUtc(Path.Join(gitDir, "logs", "HEAD"), old);
            File.SetLastWriteTimeUtc(Path.Join(main, ".git", "logs", "refs", "heads", "old"), old);
            return new(main, parent, checkout, Path.Join(checkout, "source.txt"), old);
        }
        public static void Age(string root, DateTime old)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, old);
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) Directory.SetLastWriteTimeUtc(directory, old);
            Directory.SetLastWriteTimeUtc(root, old);
        }
        public static async Task AgeReflogs(string checkout, string main, string branch, DateTime old)
        {
            string gitDir = (await Run(checkout, old, "rev-parse", "--absolute-git-dir")).Trim();
            File.SetLastWriteTimeUtc(Path.Join(gitDir, "logs", "HEAD"), old);
            File.SetLastWriteTimeUtc(Path.Join(main, ".git", "logs", "refs", "heads", branch), old);
        }
        public static async Task<string> Run(string root, DateTime date, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; info.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            info.Environment["GIT_AUTHOR_NAME"] = "Owned fixture"; info.Environment["GIT_COMMITTER_NAME"] = "Owned fixture";
            info.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid"; info.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";
            info.Environment["GIT_AUTHOR_DATE"] = date.ToString("O", CultureInfo.InvariantCulture);
            info.Environment["GIT_COMMITTER_DATE"] = date.ToString("O", CultureInfo.InvariantCulture);
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            string stdout = await output, stderr = await error;
            Assert.True(process.ExitCode == 0, stderr); return stdout;
        }
    }
}
