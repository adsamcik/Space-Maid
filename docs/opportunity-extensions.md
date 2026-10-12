# Opportunities extensions

`SpaceMaid.Extensions.Developer` is the bundled example: all development detectors, cleanup descriptions, icons and project/language/tool grouping live there. The scanner and host do not reference its implementation assembly. An extension may supply a separate WPF view, Opportunities groups, or both. Returning `null` from `CreateView()` contributes groups without adding another workflow window.

## Package

Target `net10.0-windows`, enable WPF and `EnableDynamicLoading`, and reference `SpaceMaid.Extensions.Contracts`. Core model types arrive through that reference; Contracts, Core and WPF are shared with the host, while private dependencies use the package's load context. Do not reference `SpaceMaid.App`. Use the same Contracts/Core build as the host. API version 1 supports the optional capability without changing existing Photos/Apps entrypoints.

Ship `extension.json`, the entry DLL, its `.deps.json` and private dependencies. The entry type implements `ISpaceMaidExtension` and `IOpportunityExtension`. For example:

```json
{
  "id": "personal.preview-cache",
  "name": "Preview cache",
  "version": "1.0.0",
  "contractVersion": 1,
  "entryAssembly": "Personal.PreviewCache.dll",
  "entryType": "Personal.PreviewCache.Extension"
}
```

Add the built directory through **Extensions → Add extension**, then restart. Enabling/disabling takes effect after restart. Bundled Developer, Photos and Apps use the same loader. Packages run trusted local code with the user's permissions; dependency isolation is not a security sandbox. The elevated scan helper loads no plugins.

## Metadata analysis

`Analyze(IOpportunityInventory, CancellationToken)` receives the completed scan's indexed metadata. Names are spans, objects and name links have distinct indexes, child links are enumerated on demand, and full paths are resolved only when needed. Read directory sizes with `LogicalSize`/`AllocatedSize`. Keep analysis cancellable and metadata-only: do not walk the disk, read contents, hydrate cloud files, launch tools or create a second drive-sized path inventory.

Return `OpportunityAnalysis(Findings, Groups)`. Each finding references a resolved directory index and has a stable `RuleId`, title, category, reason, caution and suggested recovery action. Supply evidence stronger than a generic folder name. Age or an extension alone never proves a folder disposable. One finding per directory per provider is supported; choose the most specific matching rule.

For an explicitly marked preview-cache folder, constructing a finding and its checklist group looks like this (the caller has already verified the owning tool's marker in `inventory.Children(directory)`):

```csharp
var finding = new Opportunity(directory, "Preview cache", "Media previews",
    inventory.PathForFile(directory)!, inventory.LogicalSize(directory),
    inventory.AllocatedSize(directory), "Contains the owning tool's cache marker.",
    "Keep any previews edited by hand.", "Rebuild previews in the owning tool.", true)
{
    RuleId = "owned-preview-cache-v1",
    Appearance = OpportunityAppearance.Cache
};
var group = new OpportunityGroup("cleanup:previews", "Preview caches", "",
    "Review first", [finding], 0)
{
    Caption = "Generated previews",
    StatusLabel = "Review first",
    Glyph = "\uE91B"
};
return new OpportunityAnalysis([finding], new OpportunityGroupSet([], [], Cleanup: [group]));
```

The host stamps `ProviderId`, derives paths/sizes from the inventory and recomputes group totals. Group members must belong to that provider's findings; child membership must be a subset of its parent. Keys must be unique throughout each group hierarchy. Invalid output discards that provider's analysis and logs the failure while other providers continue. Use stable local keys beginning `cleanup:` for checklist groups; the host changes `cleanup:previews` into `cleanup:personal.preview-cache/previews`. Developer keeps its historical keys for saved recommendations. Keep resulting checklist keys under 192 characters. Projects/types have separate namespaced keys. Group captions, glyphs, status labels, `CanSelectGroup` and optional children drive the shared UI; empty groups are omitted.

Analysis results are cached only for their immutable scan generation. The initial analysis and each fresh inventory's analysis run off the dispatcher. `BuildCleanupGroups` is optional; its default returns `Groups.Cleanup`. An override can cheaply regroup existing findings using `OpportunityGroupContext` (the shared worktree policy and checked matching paths). This synchronous regrouping runs during view/filter updates: keep it bounded, free of disk/tool work, and use the supplied analysis. General extensions need no override. Developer uses it for the existing worktree rule/age controls. All calls honor cancellation and complete before extension disposal. Enforce practical resource limits in trusted extension code; arbitrary code can still block or crash the host.

## Shared cleanup

The extension proposes folders; it receives no deletion or confirmation service. Review rereads the selected scopes and their parent metadata, then rediscovers rules through the current catalog. Preparation matches provider, rule, directory, category, title and path. Execution checks classification again on fresh metadata and validates the captured native target identity before recycling. An absent/failing provider, changed marker, replacement folder or mismatched rule is excluded. Supplying no discovery callback fails closed.

The host retains exact-path confirmation, cancellation/progress, Recycle Bin-only behavior, Git worktree safeguards, target validation and nested/hard-link accounting. `IsWorktree` and group `Role` participate in the shared check/filter UI; they do not bypass Git checks. Group sizes are observed allocation, not guaranteed savings, and recycling occupies space until the bin is emptied. Photos cleanup remains disabled.

Mark build findings with `IsBuildOutput = true` so the host applies recent-activity protection. Set `IsRegeneratable = true` only for a recognized generated layout with a known rebuild action; generic `build`/`dist` names do not qualify. Other findings with `RequiresManualReview` enter **Needs review**. Review starts with **Ready** folders selected, permits narrowing that captured plan, and never selects **Kept** folders. Group membership and labels come from the provider; group selection is limited to the visible state and search filters.

Build retention defaults to three days and is configurable from 1–365 days in Opportunities' **View options**. A plan captures that policy. The host considers creation/write dates in the output subtree and direct files of its parent, keeps unknown/incomplete/reparse activity, and sends source/project markers inside outputs to manual review. This is observed activity, not actual last use; deeper source folders and active processes are not tracked. Selected ancestors containing protected outputs are kept too. Execution rechecks classification and relevant output/parent activity before each move, retaining the original confirmed native identities. A setting read/save failure blocks review until explicitly repaired.

Native recycling success requires the confirmed folder identity at Windows' exact same-volume Recycle Bin destination and an absent original path. Progress or a successful Shell return code alone does not establish completion. Permanent-delete fallback remains blocked, with native error codes and callback evidence in the action log.

Register custom providers in catalog tests, then use the real manifest loader to verify packaging. Existing `OpportunityExtensionTests` exercises viewless Developer loading, restart disablement, independent provider keys/membership, failure isolation and fail-closed review/execution. `DesktopTests` covers the actual shared checklist, selection, breakdowns, refresh and confirmed recycling with owned fixtures.

Bundled extensions are listed once as `BundledExtension` items in `src/SpaceMaid.App/SpaceMaid.App.csproj`, with their project path and `ExtensionId`. The shared packaging target copies each extension's dependencies and native assets into `Extensions/<id>`, excluding host-shared Core and Contracts assemblies. ScanHelper and the assistant's self-contained Worker retain separate packaging rules.

Desktop workflow checks live in `Desktop*Checks.cs`; `DesktopHarness.cs` contains rendering, routed-input and dispatcher helpers. `DesktopTests.cs` owns the single Application and ordered workflow calls. Keep those calls sequential and retain their timeouts when adding checks.
