namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// What the agent-configuration step did, in the shape the host and webview read: the client files it copied and one
/// row per merged file. The merged text itself is never part of the payload.
/// </summary>
public sealed record AgentConfigPayload(IReadOnlyList<string> Copied, IReadOnlyList<AgentFilePayload> Files)
{
    public static AgentConfigPayload From(AgentConfigSummary summary) =>
        new(summary.Copied, summary.Merged.Select(AgentFilePayload.From).ToList());

    /// <summary>The CLI lines for the copied files and for every merged file that changed or failed.</summary>
    public IReadOnlyList<string> Lines()
    {
        // A merge that created its file also lists it as copied; that file is described by its own line below.
        var lines = Copied
            .Where(relative => Files.All(file => file.RelativePath != relative))
            .Select(relative => $"agent config: copied {relative}")
            .ToList();
        lines.AddRange(Files.Select(file => file.Describe()).OfType<string>());
        return lines;
    }
}
