using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

// The draft and a saved review describe the same selected operations. Neither
// the category inventory nor logical file size is a promise of reclaimed space.
internal sealed record CleanupImpact(string Caption, string Action, string Consequence, bool Permanent)
{
    internal static CleanupImpact Describe(CleanupPreview preview, BuildCleanupAction buildAction)
    {
        int count = preview.Locations.Count;
        int deleted = buildAction == BuildCleanupAction.PermanentlyDelete ? preview.Locations.Count(item => item.IsBuildOutput) : 0;
        int recycled = count - deleted;
        string caption = preview.UsesLogicalSize ? "Selected file size · disk savings unknown"
            : recycled == 0 ? "Estimated space to free"
            : deleted == 0 ? "Potential space after emptying the Recycle Bin"
            : "Potential space after cleanup and emptying the Recycle Bin";
        string action = count == 0 ? "Choose folders to clean"
            : deleted == 0 ? $"Recycle {Folders(count)}"
            : recycled == 0 ? $"Delete {Folders(count)} permanently" : $"Clean {Folders(count)}";
        var consequences = new List<string>();
        if (deleted > 0) consequences.Add($"{Folders(deleted)} will be permanently deleted. No recovery.");
        if (recycled > 0) consequences.Add($"{Folders(recycled)} will move to the Recycle Bin. Recoverable until you empty it to free space.");
        if (count == 0) consequences.Add("No folders selected. Nothing will be removed.");
        return new(caption, action, string.Join(" ", consequences), deleted > 0);
    }

    private static string Folders(int count) => $"{count:N0} {(count == 1 ? "folder" : "folders")}";
}
