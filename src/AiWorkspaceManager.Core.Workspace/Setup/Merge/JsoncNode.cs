namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// A span of the document, by UTF-8 byte offset: an object, an array or a scalar. Objects keep their
/// members and arrays their elements, each with the offsets the text edits need.
/// </summary>
internal sealed class JsoncNode
{
    public JsoncNode(bool isObject, bool isArray, int start)
    {
        IsObject = isObject;
        IsArray = isArray;
        Start = start;
    }

    public bool IsObject { get; }

    public bool IsArray { get; }

    /// <summary>Offset of the first byte: the opening brace or bracket, or the first byte of a scalar.</summary>
    public int Start { get; }

    /// <summary>Offset just past the last byte of the value.</summary>
    public int End { get; set; }

    public List<JsoncMember> Members { get; } = [];

    public List<JsoncNode> Elements { get; } = [];
}
