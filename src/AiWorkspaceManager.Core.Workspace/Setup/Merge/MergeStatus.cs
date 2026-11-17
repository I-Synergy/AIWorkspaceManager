namespace AiWorkspaceManager.Core.Workspace;

/// <summary>What a merge did to one client file.</summary>
public enum MergeStatus
{
    /// <summary>The file was missing and is created from the template.</summary>
    Created,

    /// <summary>The file existed and at least one key, item or pinned value was changed.</summary>
    Merged,

    /// <summary>The file already holds everything the template ships; nothing is written.</summary>
    Unchanged,

    /// <summary>The project file or the template fragment is not a valid JSON object; nothing is written.</summary>
    Error,
}
