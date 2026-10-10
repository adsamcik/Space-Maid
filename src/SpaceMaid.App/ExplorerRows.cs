using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using SpaceMaid.Core.Analysis;
using SpaceMaid.Core.Scanning;

namespace SpaceMaid.App;

internal sealed record ExplorerRow(ScanSnapshot Scan, ScanEntry Entry, int Depth, bool Expanded, int VisibleIndex = -1)
{
    public string Name => Entry.Name;
    public string Path => Scan.EntryPath(Entry.LinkIndex) ?? "Unresolved path";
    public string TypeDescription => Entry.IsDirectory ? "" : FileTypeClassifier.Describe(Name);
    public string EntryTooltip => TypeDescription.Length > 0 ? $"{Path}\n{TypeDescription}" : Path;
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public bool CanExpand => Entry.IsDirectory && Entry.IsPrimaryName;
    public string Expander => CanExpand ? Expanded ? "⌄" : "›" : "";
    public string Glyph => Entry.IsDirectory ? "\uE8B7" : "\uE8A5";
    public string Allocated => Scan.HasAllocationData ? SizeText.Format(Entry.AttributedBytes(true)) : "—";
    public string Logical => SizeText.Format(Entry.LogicalBytes);
    public string Share
    {
        get
        {
            if (Entry.ParentIndex < 0) return "—";
            var parent = Scan.Entry(Scan.PrimaryLink(Entry.ParentIndex));
            long total = parent.AttributedBytes(Scan.HasAllocationData);
            return total == 0 ? "—" : $"{100d * Entry.AttributedBytes(Scan.HasAllocationData) / total:0.#}%";
        }
    }
    public string Flags => string.Join(" · ", new[] { Entry.IsShared ? "Shared" : null, Entry.IsIncomplete ? "Partial" : null }.OfType<string>());
    public string Description => $"{Path}, {Allocated} allocated, {Logical} logical. {Flags} {TypeDescription}";
    public string SizeNote => Entry.IsShared
        ? $"This object has multiple observed names. Its {SizeText.Format(Entry.AllocatedBytes)} allocation is attributed once, under {Scan.EntryPath(Scan.PrimaryLink(Entry.FileIndex))}."
        : Entry.IsIncomplete ? "This branch is incomplete. Sizes show only metadata that was read." : "";
}

// WPF receives an indexed source, not millions of row objects. Only expanded
// branches occupy this compact list; recycled UI containers request rows on demand.
internal sealed class ExplorerRows(ScanSnapshot scan) : IList, INotifyCollectionChanged
{
    private readonly record struct Position(int Link, int Depth);
    private readonly List<Position> _visible = [new(-1, 0)];
    private readonly HashSet<int> _expanded = [];
    public ScanSnapshot Scan { get; } = scan;
    public int Count => _visible.Count;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public ExplorerRow Row(int index)
    {
        var position = _visible[index];
        return new ExplorerRow(Scan, Scan.Entry(position.Link), position.Depth, _expanded.Contains(position.Link), index);
    }
    public int Find(int link) => _visible.FindIndex(position => position.Link == link);
    public bool IsExpanded(int link) => _expanded.Contains(link);
    public string[] ExpandedPaths() => _expanded.Select(Scan.EntryPath).OfType<string>().ToArray();

    public void Expand(int link, int[] children)
    {
        int index = Find(link);
        if (index < 0 || !_expanded.Add(link)) return;
        int depth = _visible[index].Depth + 1;
        _visible.InsertRange(index + 1, children.Select(child => new Position(child, depth)).ToArray());
        Changed();
    }

    public void Collapse(int link)
    {
        int index = Find(link);
        if (index < 0 || !_expanded.Remove(link)) return;
        int end = index + 1;
        while (end < Count && _visible[end].Depth > _visible[index].Depth)
        { _expanded.Remove(_visible[end].Link); end++; }
        _visible.RemoveRange(index + 1, end - index - 1);
        Changed();
    }

    public void Reorder(IReadOnlyDictionary<int, int[]> childOrders, CancellationToken token = default)
    {
        var reordered = new List<Position>(_visible.Count);
        var pending = new Stack<Position>();
        pending.Push(new(-1, 0));
        while (pending.TryPop(out var position))
        {
            if ((reordered.Count & 4095) == 0) token.ThrowIfCancellationRequested();
            reordered.Add(position);
            if (!_expanded.Contains(position.Link)) continue;
            int directory = Scan.Entry(position.Link).FileIndex;
            var children = childOrders[directory];
            for (int i = children.Length - 1; i >= 0; i--) pending.Push(new(children[i], position.Depth + 1));
        }
        token.ThrowIfCancellationRequested();
        _visible.Clear();
        _visible.AddRange(reordered);
        Changed();
    }
    private void Changed()
    {
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    }

    public object? this[int index] { get => Row(index); set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public int IndexOf(object? value)
    {
        if (value is not ExplorerRow row || !ReferenceEquals(row.Scan, Scan)) return -1;
        int index = row.VisibleIndex;
        return (uint)index < (uint)Count && _visible[index].Link == row.Entry.LinkIndex ? index : Find(row.Entry.LinkIndex);
    }
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return Row(i); }
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(Row(i), index + i); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
