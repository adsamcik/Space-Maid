using System.ComponentModel;
using SpaceMaid.Core.Analysis;

namespace SpaceMaid.App;

internal sealed class CleanupReviewRow : INotifyPropertyChanged
{
    private readonly Action<CleanupReviewRow>? _selectionChanged;
    private readonly bool _hasAllocation;
    private bool _selected;
    private string _status;
    private CleanupReadiness? _resultState;
    public Opportunity Location { get; }
    public BuildCleanupAssessment Assessment { get; }
    public CleanupReadiness DisplayState => _resultState ?? Assessment.State;
    public (string Provider, string Rule, string Path) Identity => (Location.ProviderId, Location.RuleId, Location.Path);
    public string Title => Location.Title;
    public string FolderName
    {
        get
        {
            string path = Location.Path.TrimEnd('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            string? parent = System.IO.Path.GetDirectoryName(path);
            string project = parent is null ? "" : System.IO.Path.GetFileName(parent);
            return project.Length > 0 ? project + " / " + name : name.Length > 0 ? name : Location.Path;
        }
    }
    public string Size => SizeText.Format(Bytes);
    public long Bytes => Location.DisplayBytes(_hasAllocation);
    public string Path => Location.Path;
    public string Reason => Location.Reason;
    public string Caution => Location.Caution;
    public string SuggestedAction => Location.SuggestedAction;
    public string DecisionReason => Assessment.Reason;
    public string Activity => Assessment.LastActivityUtc?.ToLocalTime().ToString("dd MMM yyyy") ?? "—";
    public string ActivityTooltip => Assessment.LastActivityUtc is { } activity
        ? activity.ToLocalTime().ToString("g") + " · Latest file change; access times are not reliable."
        : "Activity is unknown.";
    public bool CanSelect { get; private set; }
    public string SelectionHint => $"Select {Path}";
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }
    public bool Selected
    {
        get => _selected;
        set
        {
            if (!CanSelect || _selected == value) return;
            _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); _selectionChanged?.Invoke(this);
        }
    }
    public CleanupReviewRow(Opportunity location, bool hasAllocation, BuildCleanupAssessment assessment,
        bool selected, bool canSelect, Action<CleanupReviewRow>? selectionChanged = null)
    {
        Location = location; _hasAllocation = hasAllocation; Assessment = assessment;
        CanSelect = canSelect && assessment.State != CleanupReadiness.Protected;
        _selected = selected; _selectionChanged = selectionChanged;
        _status = assessment.State switch { CleanupReadiness.Protected => "Kept", CleanupReadiness.NeedsReview => "Needs review", _ => "Ready" };
    }
    public void DisableSelection()
    { CanSelect = false; PropertyChanged?.Invoke(this, new(nameof(CanSelect))); }
    public void SetResult(CleanupLocationResult? result)
    {
        _resultState = result?.Outcome == CleanupOutcome.Recycled ? CleanupReadiness.Ready
            : !Selected || Assessment.State == CleanupReadiness.Protected ? CleanupReadiness.Protected : CleanupReadiness.NeedsReview;
        Status = result is null ? "Kept · Not selected"
            : result.Outcome == CleanupOutcome.Recycled ? "Moved to Recycle Bin"
            : result.Outcome switch
            {
                CleanupOutcome.Skipped => "Skipped · " + result.Message,
                CleanupOutcome.NotProcessed => "Not started · " + result.Message,
                _ => result.Outcome + " · " + result.Message
            };
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class CleanupReviewGroup : INotifyPropertyChanged
{
    private readonly Action<CleanupReviewGroup, bool> _selectionChanged;
    public string Key { get; }
    public string Name { get; }
    public IReadOnlyList<CleanupReviewRow> Rows { get; }
    public IReadOnlyList<CleanupReviewRow> Matches { get; private set; } = [];
    public string Count => $"{Matches.Count:N0} {(Matches.Count == 1 ? "folder" : "folders")}";
    public long Bytes => Matches.Sum(row => row.Bytes);
    public string Size => SizeText.Format(Bytes);
    public double RelativeSize { get; private set; }
    public bool CanSelect => Matches.Any(row => row.CanSelect);
    public string SelectionHint => $"Select all {Matches.Count(row => row.CanSelect):N0} matching folders in {Name}";
    public bool? Checked
    {
        get
        {
            int count = 0, selected = 0;
            foreach (var row in Matches) if (row.CanSelect) { count++; if (row.Selected) selected++; }
            return selected == 0 ? false : selected == count ? true : null;
        }
        set => _selectionChanged(this, value == true || Checked is null);
    }
    public CleanupReviewGroup(string key, string name, IReadOnlyList<CleanupReviewRow> rows,
        Action<CleanupReviewGroup, bool> selectionChanged)
    { Key = key; Name = name; Rows = rows; _selectionChanged = selectionChanged; }
    public void SetMatches(IReadOnlyList<CleanupReviewRow> matches, long largestBytes)
    {
        Matches = matches;
        RelativeSize = largestBytes > 0 ? Math.Clamp(100d * Bytes / largestBytes, 0, 100) : 0;
        foreach (string property in new[] { nameof(Count), nameof(Size), nameof(RelativeSize), nameof(CanSelect), nameof(SelectionHint), nameof(Checked) })
            PropertyChanged?.Invoke(this, new(property));
    }
    public void NotifySelection() => PropertyChanged?.Invoke(this, new(nameof(Checked)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
