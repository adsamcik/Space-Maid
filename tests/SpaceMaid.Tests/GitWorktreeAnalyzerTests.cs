using System.Diagnostics;
using System.Globalization;
using System.IO;
using SpaceMaid.App;
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
        var result = await new FileOperations().AssessWorktreeAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(result.Recommended);
        Assert.True(result.Status == "Local changes", $"{result.Status}: {result.Reason}");
        Assert.False(result.VerifiedClean);
        foreach (var mode in Enum.GetValues<WorktreeCleanupMode>())
            Assert.False(new WorktreeCleanupPolicy(mode, 1).Matches(result, DateTime.UtcNow));
        Assert.Equal("local work that must be preserved", File.ReadAllText(changed));
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task IndexFlagsThatCanHideLocalChangesRequireReview(string flag)
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        await GitFixture.Run(repo.Checkout, repo.Old, "update-index", flag, "--", "source.txt");
        File.WriteAllText(repo.Source, "local work hidden from ordinary Git status");
        GitFixture.Age(repo.Checkout, repo.Old);
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        string index = Path.Join(gitDir, "index"); byte[] before = File.ReadAllBytes(index);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var result = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.False(result.Recommended); Assert.False(result.VerifiedClean);
        Assert.Equal("Needs review", result.Status);
        Assert.Contains("index entries need manual review", result.Reason, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal("local work hidden from ordinary Git status", File.ReadAllText(repo.Source));
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
        var plan = CleanupPlanner.Prepare(fresh, selected, blockedPaths: blocked, discover: DeveloperOpportunityFixture.Find);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedAgentWorktreesRequireRetainedCommitsAndFreshCleanState(bool localMainFallback)
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        await GitFixture.Run(repo.Checkout, repo.Old, "switch", "--detach");
        if (localMainFallback)
            await GitFixture.Run(repo.Main, repo.Old, "symbolic-ref", "--delete", "refs/remotes/origin/HEAD");
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        File.SetLastWriteTimeUtc(Path.Join(gitDir, "logs", "HEAD"), repo.Old);
        GitFixture.Age(repo.Checkout, repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var location = Finding(scan, repo.Checkout);
        var assessment = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.True(assessment.IsDetached); Assert.True(assessment.VerifiedClean, assessment.Reason);
        Assert.True(assessment.Integrated, assessment.Reason); Assert.True(assessment.Recommended, assessment.Reason);
        Assert.Equal("Detached HEAD", assessment.Branch);
        Assert.Equal(localMainFallback ? "refs/heads/main" : "refs/remotes/origin/main", assessment.IntegrationTarget);
        Assert.Empty(await GitWorktreeGuard.CheckAsync(scan, [location], [repo.Checkout]));

        File.AppendAllText(repo.Source, "\nunique detached work");
        await GitFixture.Run(repo.Checkout, repo.Old, "add", "--", "source.txt");
        await GitFixture.Run(repo.Checkout, repo.Old, "commit", "-m", "owned detached commit");
        File.SetLastWriteTimeUtc(Path.Join(gitDir, "logs", "HEAD"), repo.Old);
        GitFixture.Age(repo.Checkout, repo.Old);
        var unique = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.True(unique.VerifiedClean, unique.Reason); Assert.False(unique.Integrated); Assert.True(unique.IsDetached);
        foreach (var mode in Enum.GetValues<WorktreeCleanupMode>())
            Assert.False(new WorktreeCleanupPolicy(mode, 1).Matches(unique, DateTime.UtcNow));
        var blocked = await GitWorktreeGuard.CheckAsync(scan, [location],
            new Dictionary<string, WorktreeCleanupPolicy> { [repo.Checkout] = new(WorktreeCleanupMode.Old, 1) });
        Assert.Contains(repo.Checkout, blocked.Keys);
        Assert.Contains("unique detached work", File.ReadAllText(repo.Source), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachedWorktreesKeepRecentActivityUntrackedFilesAndMissingIntegrationEvidence()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        await GitFixture.Run(repo.Checkout, DateTime.UtcNow, "switch", "--detach");
        GitFixture.Age(repo.Checkout, repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var location = Finding(scan, repo.Checkout);
        var recent = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.True(recent.IsDetached); Assert.True(recent.Integrated); Assert.False(recent.Recommended);
        Assert.True(recent.LastActivityUtc > DateTime.UtcNow.AddDays(-1));
        string untracked = Path.Join(repo.Checkout, "keep-me.txt"); File.WriteAllText(untracked, "unique local data");
        var changed = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.False(changed.VerifiedClean); Assert.Equal("Local changes", changed.Status);
        File.Delete(untracked);
        await GitFixture.Run(repo.Main, repo.Old, "symbolic-ref", "--delete", "refs/remotes/origin/HEAD");
        await GitFixture.Run(repo.Main, repo.Old, "branch", "-m", "main", "personal");
        var unavailable = await GitWorktreeAnalyzer.AssessAsync(scan, location);
        Assert.False(unavailable.Integrated); Assert.Null(unavailable.IntegrationTarget);
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Old, 1).Matches(unavailable, DateTime.UtcNow));
    }

    private static Opportunity Finding(ScanSnapshot scan, string path) => Assert.Single(OpportunityDetector.Find(scan), finding => finding.Path == path);

    [Fact]
    public async Task FeatureTrackingItsOwnRemoteRefIsRetainedButNotIntegratedIntoTheApprovedTarget()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        File.AppendAllText(repo.Source, "\nfeature not merged into main");
        await GitFixture.Run(repo.Checkout, repo.Old, "commit", "-am", "unmerged feature");
        await GitFixture.Run(repo.Main, repo.Old, "config", "remote.origin.url", ".");
        await GitFixture.Run(repo.Main, repo.Old, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        await GitFixture.Run(repo.Main, repo.Old, "update-ref", "refs/remotes/origin/old", "refs/heads/old");
        await GitFixture.Run(repo.Checkout, repo.Old, "branch", "--set-upstream-to=origin/old", "old");
        GitFixture.Age(repo.Checkout, repo.Old); await GitFixture.AgeReflogs(repo.Checkout, repo.Main, "old", repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var value = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), "refs/heads/main");
        Assert.True(value.RetainedOnUpstream); Assert.Equal("refs/remotes/origin/old", value.UpstreamReference);
        Assert.False(value.Integrated); Assert.True(value.IntegrationTargetConfirmed); Assert.Equal("refs/heads/main", value.IntegrationTarget);
        Assert.False(value.Recommended);
        var suggested = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout));
        Assert.Equal("refs/remotes/origin/main", suggested.IntegrationTarget); Assert.False(suggested.Integrated);
    }

    [Fact]
    public async Task IgnoredLocalSecretsAndRecentCopiesDoNotQualifyForRetirement()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        File.WriteAllText(Path.Join(repo.Checkout, ".gitignore"), ".env\n");
        await GitFixture.Run(repo.Checkout, repo.Old, "add", ".gitignore");
        await GitFixture.Run(repo.Checkout, repo.Old, "commit", "-m", "ignore local secrets");
        await GitFixture.Run(repo.Main, repo.Old, "merge", "--ff-only", "old");
        File.WriteAllText(Path.Join(repo.Checkout, ".env"), "local credentials must survive");
        GitFixture.Age(repo.Checkout, repo.Old); await GitFixture.AgeReflogs(repo.Checkout, repo.Main, "old", repo.Old);
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var ignored = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), "refs/heads/main");
        Assert.True(ignored.VerifiedClean); Assert.True(ignored.HasIgnoredFiles); Assert.Contains(".env", ignored.IgnoredPaths);
        Assert.False(new WorktreeCleanupPolicy(WorktreeCleanupMode.Integrated).Matches(ignored, DateTime.UtcNow));
        File.Delete(Path.Join(repo.Checkout, ".env"));
        File.SetCreationTimeUtc(repo.Source, DateTime.UtcNow); File.SetLastWriteTimeUtc(repo.Source, repo.Old);
        scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var copied = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), "refs/heads/main");
        Assert.True(copied.Integrated); Assert.False(copied.Recommended); Assert.True(copied.LastActivityUtc > DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public async Task OperationStateIsExplicitAndLargeUnrelatedObjectDirectoriesAreNotLeased()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string unrelated = Directory.CreateDirectory(Path.Join(repo.Main, ".git", "objects", "research-padding")).FullName;
        for (int i = 0; i < 4200; i++) File.WriteAllText(Path.Join(unrelated, i.ToString(CultureInfo.InvariantCulture)), "unrelated");
        var scan = await new VolumeScanner().ScanAsync(fixture.Path, preferFast: false);
        var value = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), "refs/heads/main");
        Assert.True(value.Recommended, value.Reason);
        string gitDir = (await GitFixture.Run(repo.Checkout, repo.Old, "rev-parse", "--absolute-git-dir")).Trim();
        Directory.CreateDirectory(Path.Join(gitDir, "rebase-merge"));
        var active = await GitWorktreeAnalyzer.AssessAsync(scan, Finding(scan, repo.Checkout), "refs/heads/main");
        Assert.True(active.OperationInProgress); Assert.False(active.Recommended);
    }

    [Fact]
    public async Task ManagedQuarantineAndRestorePreserveTheExactBranchAndPauseAutomaticRetirement()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        var service = new WorktreeRetirement(Path.Join(fixture.Path, "retirement.json"));
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 7, true);
        var retired = await service.QuarantineAsync(policy);
        Assert.Equal(WorktreeRetirementState.Quarantined, retired.State); Assert.False(Directory.Exists(repo.Checkout));
        Assert.True(Directory.Exists(retired.QuarantinePath)); Assert.Equal("owned tracked source", File.ReadAllText(Path.Join(retired.QuarantinePath, "source.txt")));
        Assert.Equal(retired.Head, (await GitFixture.Run(repo.Main, repo.Old, "rev-parse", retired.RecoveryReference)).Trim());
        Assert.Equal(retired.Head, (await GitFixture.Run(repo.Main, repo.Old, "rev-parse", "old")).Trim());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(retired.Id));
        var restored = await service.RestoreAsync(retired.Id);
        Assert.Equal(WorktreeRetirementState.Restored, restored.State); Assert.True(Directory.Exists(repo.Checkout));
        Assert.False(Assert.Single(service.Load().Policies).Automatic);
        Assert.Contains(repo.Checkout.Replace('\\', '/'), (await GitFixture.Run(repo.Main, repo.Old, "worktree", "list", "--porcelain")).Replace('\\', '/'));
    }

    [Fact]
    public async Task FinalizationKeepsChangesAndNeverRemovesNewLocalData()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy); Assert.Equal(WorktreeRetirementState.Quarantined, record.State);
        var state = service.Load();
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(state with { Records = [record with { QuarantinedUtc = DateTime.UtcNow.AddDays(-2) }] }));
        string newFile = Path.Join(record.QuarantinePath, "new-local-work.txt"); File.WriteAllText(newFile, "preserve new work");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id));
        Assert.Equal("preserve new work", File.ReadAllText(newFile)); Assert.True(File.Exists(Path.Join(record.QuarantinePath, "source.txt")));
        File.Delete(newFile);
        File.AppendAllText(Path.Join(record.QuarantinePath, "source.txt"), "\nnew change");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(record.Id));
        Assert.Contains("new change", File.ReadAllText(Path.Join(record.QuarantinePath, "source.txt")));
    }

    [Fact]
    public async Task FinalizationDeletesOnlyCapturedObjectsAndRetainsCommitHistory()
    {
        using var fixture = new TestDirectory(); using var repo = await GitFixture.Create(fixture.Path);
        string settings = Path.Join(fixture.Path, "retirement.json"); var service = new WorktreeRetirement(settings);
        var policy = await service.EnrollAsync(repo.Checkout, "refs/heads/main", 30, 1, false);
        var record = await service.QuarantineAsync(policy); Assert.Equal(WorktreeRetirementState.Quarantined, record.State);
        var state = service.Load();
        File.WriteAllText(settings, System.Text.Json.JsonSerializer.Serialize(state with { Records = [record with { QuarantinedUtc = DateTime.UtcNow.AddDays(-2) }] }));
        var final = await service.FinalizeAsync(record.Id);
        Assert.Equal(WorktreeRetirementState.Completed, final.State);
        Assert.False(Directory.Exists(record.QuarantinePath));
        Assert.Equal(record.Head, (await GitFixture.Run(repo.Main, repo.Old, "rev-parse", "old")).Trim());
        Assert.Equal("owned tracked source", (await GitFixture.Run(repo.Main, repo.Old, "show", record.RecoveryReference + ":source.txt")).Trim());
        Assert.DoesNotContain(record.QuarantinePath.Replace('\\', '/'), (await GitFixture.Run(repo.Main, repo.Old, "worktree", "list", "--porcelain")).Replace('\\', '/'));
    }

    internal sealed record GitFixture(string Main, string Parent, string Checkout, string Source, DateTime Old) : IDisposable
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
            await AgeReflogs(checkout, main, "old", old);
            return new(main, parent, checkout, Path.Join(checkout, "source.txt"), old);
        }
        public static void Age(string root, DateTime old)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            { File.SetCreationTimeUtc(file, old); File.SetLastWriteTimeUtc(file, old); }
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root))
            { Directory.SetCreationTimeUtc(directory, old); Directory.SetLastWriteTimeUtc(directory, old); }
        }
        public static async Task AgeReflogs(string checkout, string main, string branch, DateTime old)
        {
            string gitDir = (await Run(checkout, old, "rev-parse", "--absolute-git-dir")).Trim();
            Age(gitDir, old);
            File.SetLastWriteTimeUtc(Path.Join(gitDir, "logs", "HEAD"), old);
            File.SetLastWriteTimeUtc(Path.Join(main, ".git", "logs", "refs", "heads", branch), old);
            File.SetCreationTimeUtc(Path.Join(main, ".git", "logs", "refs", "heads", branch), old);
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
