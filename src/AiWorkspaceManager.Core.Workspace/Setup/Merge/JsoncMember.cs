namespace AiWorkspaceManager.Core.Workspace;

/// <summary>An object member: its name, the offset of the name's opening quote and the value's span.</summary>
internal sealed record JsoncMember(string Name, int NameStart, JsoncNode Value);
