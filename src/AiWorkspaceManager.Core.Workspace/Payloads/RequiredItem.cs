namespace AiWorkspaceManager.Core.Workspace;

/// <summary>An item selected because <paramref name="RequiredBy"/> declares it in <c>requires:</c>.</summary>
public sealed record RequiredItem(string Id, string Kind, string RequiredBy);
