namespace JNotes.Core;

/// <summary>In-memory view over the tree entries: children lookup, ancestry, display paths.</summary>
public sealed class NoteTreeIndex
{
    private readonly Dictionary<Guid, TreeEntry> _byId;
    private readonly Dictionary<Guid, List<TreeEntry>> _children; // Guid.Empty = root

    public NoteTreeIndex(IEnumerable<TreeEntry> entries)
    {
        var list = entries.ToList();
        _byId = list.ToDictionary(e => e.Id);
        _children = list.GroupBy(e => e.ParentId ?? Guid.Empty).ToDictionary(g => g.Key, g => g.ToList());
    }

    public bool IsEmpty => _byId.Count == 0;

    public TreeEntry? Find(Guid id) => _byId.GetValueOrDefault(id);

    /// <summary>Children in display order (custom order; unordered items folders first, then alphabetical).</summary>
    public IReadOnlyList<TreeEntry> ChildrenOf(Guid? parentId) =>
        _children.TryGetValue(parentId ?? Guid.Empty, out var c) ? c : [];

    /// <summary>Folder ancestors of an entry, nearest-root first.</summary>
    public IReadOnlyList<TreeEntry> AncestorsOf(Guid id)
    {
        var result = new List<TreeEntry>();
        var seen = new HashSet<Guid>();
        for (var parent = Find(id)?.ParentId; parent is { } p && seen.Add(p) && Find(p) is { } f; parent = f.ParentId)
            result.Add(f);
        result.Reverse();
        return result;
    }

    /// <summary>All entries beneath a folder (not including the folder itself).</summary>
    public IEnumerable<TreeEntry> DescendantsOf(Guid folderId)
    {
        foreach (var child in ChildrenOf(folderId))
        {
            yield return child;
            if (child.IsFolder)
                foreach (var d in DescendantsOf(child.Id)) yield return d;
        }
    }

    public string PathOf(Guid folderId) =>
        string.Join(" / ", AncestorsOf(folderId).Select(a => a.Name).Append(Find(folderId)?.Name ?? "?"));

    /// <summary>Folders a given item may be moved into, as (id, path), sorted by path.</summary>
    public IReadOnlyList<(Guid Id, string Path)> MoveDestinationsFor(TreeEntry item)
    {
        var excluded = item.IsFolder
            ? DescendantsOf(item.Id).Where(d => d.IsFolder).Select(d => d.Id).Append(item.Id).ToHashSet()
            : [];
        return _byId.Values
            .Where(e => e.IsFolder && !excluded.Contains(e.Id))
            .Select(e => (e.Id, PathOf(e.Id)))
            .OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
