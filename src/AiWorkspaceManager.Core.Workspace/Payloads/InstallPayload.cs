namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// What <c>install</c> did. <c>Result</c> is the raw CLI outcome the webview renders;
/// <c>Dependencies</c> lists the items that were selected only because something else requires
/// them, so the host can say so without parsing the console text.
/// </summary>
public sealed record InstallPayload(
    CliResult Result,
    IReadOnlyList<RequiredItem> Dependencies,
    IReadOnlyList<SyncAction> Actions);
