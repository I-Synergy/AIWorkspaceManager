namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// One client file the setup merged, in the shape the webview shows. <c>Status</c> is one of
/// <c>created</c>, <c>merged</c>, <c>unchanged</c> or <c>error</c>; the key lists name the template keys that were
/// added, pinned to the template value, or kept at the project's value because they conflicted.
/// </summary>
public sealed record AgentFilePayload(
    string RelativePath,
    string Status,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> Conflicts,
    string? Error)
{
    public static AgentFilePayload From(AgentFileMerge merge)
    {
        var result = merge.Result;
        return new AgentFilePayload(
            merge.RelativePath,
            StatusName(result.Status),
            result.Added,
            result.Pinned,
            result.Conflicts,
            result.Error);
    }

    /// <summary>One CLI line for a file that changed or failed; unchanged files are not listed.</summary>
    public string? Describe()
    {
        if (Status == "unchanged")
        {
            return null;
        }

        if (Status == "error")
        {
            return $"agent config: {RelativePath} not merged: {Error}";
        }

        var parts = new List<string>();
        if (Added.Count > 0)
        {
            parts.Add($"added {string.Join(", ", Added)}");
        }

        if (Pinned.Count > 0)
        {
            parts.Add($"pinned {string.Join(", ", Pinned)}");
        }

        if (Conflicts.Count > 0)
        {
            parts.Add($"kept project value for {string.Join(", ", Conflicts)}");
        }

        var detail = parts.Count > 0 ? $" ({string.Join("; ", parts)})" : string.Empty;
        return $"agent config: {RelativePath} {Status}{detail}";
    }

    private static string StatusName(MergeStatus status) => status switch
    {
        MergeStatus.Created => "created",
        MergeStatus.Merged => "merged",
        MergeStatus.Unchanged => "unchanged",
        _ => "error",
    };
}
