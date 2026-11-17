namespace AiWorkspaceManager.Core.Workspace;

/// <summary>One client file the setup merged, with its path relative to the project root.</summary>
public sealed record AgentFileMerge(string RelativePath, JsoncMergeResult Result);
