namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// What <c>install</c> wrote. <c>Dependencies</c> lists the items that were selected only because
/// something else requires them, so the host can say so.
/// </summary>
public sealed record AddPayload(
    IReadOnlyList<ItemRef> Added,
    string Manifest,
    IReadOnlyList<RequiredItem> Dependencies);
