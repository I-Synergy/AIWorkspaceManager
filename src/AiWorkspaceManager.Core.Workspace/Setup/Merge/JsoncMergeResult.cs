namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// The outcome of merging one JSON(C) file: the new text to write (only when the status is
/// <see cref="MergeStatus.Created"/> or <see cref="MergeStatus.Merged"/>), the keys that were added,
/// the pinned keys whose template value replaced the project's, and the project's keys kept over a
/// conflicting template value.
/// </summary>
public sealed record JsoncMergeResult(
    MergeStatus Status,
    string Text,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> Conflicts,
    string? Error = null)
{
    /// <summary>A file that did not exist: the template text is written as it is.</summary>
    public static JsoncMergeResult Created(string text, IReadOnlyList<string> added) =>
        new(MergeStatus.Created, text, added, [], []);

    /// <summary>A file that cannot be merged; nothing is written.</summary>
    public static JsoncMergeResult Invalid(string error) =>
        new(MergeStatus.Error, string.Empty, [], [], [], error);
}
