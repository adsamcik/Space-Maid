# Extension workspace

Every loaded extension that returns a `FrameworkElement` from `CreateView()` is available directly in the main window's sidebar. The host creates that view once and retains it while switching workflows, preserving the extension's filters, selection and scroll position. Apps, Photos and Opportunities keep their established destinations; additional views need no host edits or ID registration. Extensions returning `null` can still contribute Opportunities findings.

**Extensions** opens the full-size management view. Search by name or ID, select an extension, open its workflow, or change its enabled setting. Version, load errors and location are available in its details. Adding trusted local folders and enabling/disabling apply after restart; disabling does not stop an already loaded workflow or remove its data. Loading remains blocked in an elevated host.

## Optional API v1 capabilities

Existing `ISpaceMaidExtension` and `IExtensionHost` implementations remain compatible. New interfaces are additive in `SpaceMaid.Extensions.Contracts`.

- A view can implement `IExtensionContextView.SetContext(ExtensionViewContext)` to receive the selected folder, drive root, inventory availability, stale state and foreground busy state. The host delivers changed values on the UI thread, including to hidden views. Context is informational and never authorizes analysis, content reads or cleanup. Handle expensive work explicitly; do not scan automatically on each context change.
- During initialization, cast the host to `IExtensionNavigationHost` and check its nullable `Navigation` capability. `OpenExtensionAsync(id)` selects an available extension workflow. `ExploreFolderAsync(path)` uses normal in-app folder navigation, reusing an existing inventory or scanning the chosen folder. Calls marshal to the host dispatcher and return `false` when unavailable or rejected. They grant no file-operation authority. Apps uses this for **Explore in Space Maid**, keeping **Open folder** for Explorer.
- Existing `IOpportunityExtension` contributions remain in the shared Opportunities browser. Inventory evidence, host-reviewed cleanup, bounded jobs and fixed tool adapters retain their existing contracts.

The workspace does not introduce hot loading or unloading, a general event bus, shell commands or a second plugin runtime. Extensions remain trusted local code running with the user's permissions. Specialized workflows own their result presentation and analysis state; the host owns navigation, inventory and cleanup safeguards.
