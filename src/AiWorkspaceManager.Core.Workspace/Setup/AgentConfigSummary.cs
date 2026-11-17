namespace AiWorkspaceManager.Core.Workspace;

/// <summary>What the agent-configuration step did: files copied, files left alone and the per-file merge outcomes.</summary>
public sealed record AgentConfigSummary(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<AgentFileMerge> Merged);
