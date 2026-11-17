namespace AiWorkspaceManager.Core.Workspace;

/// <summary>Collects what one file merge added, pinned and kept as a conflict.</summary>
internal sealed class MergeReport
{
    private readonly List<string> added = [];
    private readonly List<string> pinned = [];
    private readonly List<string> conflicts = [];

    public void Add(string key) => added.Add(key);

    public void Pin(string key) => pinned.Add(key);

    public void Conflict(string key) => conflicts.Add(key);

    /// <summary>The result for the edited document: merged when anything changed, otherwise unchanged.</summary>
    public JsoncMergeResult Finish(JsoncMerger editor) =>
        new(
            editor.Changed ? MergeStatus.Merged : MergeStatus.Unchanged,
            editor.Text,
            added.ToList(),
            pinned.ToList(),
            conflicts.ToList());
}
