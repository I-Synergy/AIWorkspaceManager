using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Comment-preserving JSON(C) editor. It never re-serialises the document: each operation locates the span
/// it needs with <see cref="Utf8JsonReader"/> (comments skipped, trailing commas allowed) and splices text
/// in or out, so the user's comments, trailing commas, ordering and formatting survive. Every operation
/// re-reads the current text, so a sequence of operations stays consistent.
/// </summary>
public sealed class JsoncMerger
{
    private const char ByteOrderMark = '﻿';
    private const byte Comma = (byte)',';
    private const byte Space = (byte)' ';
    private const byte Tab = (byte)'\t';

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string byteOrderMark;
    private readonly string newline;
    private readonly string indentUnit;
    private byte[] document;

    /// <summary>Starts editing <paramref name="text"/>. Throws <see cref="JsonException"/> when it is not valid JSON(C).</summary>
    public JsoncMerger(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byteOrderMark = text.Length > 0 && text[0] == ByteOrderMark ? text[..1] : string.Empty;
        var body = text[byteOrderMark.Length..];
        document = Encoding.UTF8.GetBytes(body);
        JsoncTreeParser.Parse(document);
        newline = JsoncText.DetectNewline(body);
        indentUnit = JsoncText.DetectIndent(body);
    }

    /// <summary>The document after the operations applied so far, with its byte-order mark if it had one.</summary>
    public string Text => byteOrderMark + Encoding.UTF8.GetString(document);

    /// <summary>True once an operation changed the document.</summary>
    public bool Changed { get; private set; }

    /// <summary>Parses JSON(C) text whose root must be an object; null when it is invalid or not an object.</summary>
    public static JsonObject? ParseObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var body = text.Length > 0 && text[0] == ByteOrderMark ? text[1..] : text;
        try
        {
            // The span parser also rejects duplicate keys, which JsonNode would only reject lazily.
            JsoncTreeParser.Parse(Encoding.UTF8.GetBytes(body));
            return JsonNode.Parse(body, documentOptions: ParseOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a member to the object at <paramref name="path"/>, after the existing members or, with
    /// <paramref name="first"/>, before them. Returns false when the name already exists.
    /// </summary>
    public bool AddMember(IReadOnlyList<string> path, string name, JsonNode? value, bool first = false)
    {
        var container = Locate(path);
        if (!container.IsObject)
        {
            throw new InvalidOperationException("the value at the path is not an object");
        }

        if (container.Members.Any(member => member.Name == name))
        {
            return false;
        }

        Insert(container, new Item(name, value), first);
        return true;
    }

    /// <summary>Replaces the value of an existing member (a pinned setting). Returns false when the member is missing.</summary>
    public bool ReplaceMember(IReadOnlyList<string> path, string name, JsonNode? value)
    {
        var container = Locate(path);
        var member = FindMember(container, name);
        var replacement = JsoncText.Format(value, JsoncText.LineIndent(document, member.NameStart), indentUnit, newline);
        Apply([new Edit(member.Value.Start, member.Value.End - member.Value.Start, replacement)]);
        return true;
    }

    /// <summary>Appends an element to the array at <paramref name="arrayPath"/>.</summary>
    public void AppendElement(IReadOnlyList<string> arrayPath, JsonNode? value)
    {
        var container = Locate(arrayPath);
        if (!container.IsArray)
        {
            throw new InvalidOperationException("the value at the path is not an array");
        }

        Insert(container, new Item(null, value), first: false);
    }

    /// <summary>Removes the member <paramref name="name"/> of the object at <paramref name="path"/>, with its separator and line.</summary>
    public void RemoveMember(IReadOnlyList<string> path, string name)
    {
        var container = Locate(path);
        if (!container.IsObject)
        {
            throw new InvalidOperationException("the value at the path is not an object");
        }

        var index = container.Members.FindIndex(member => member.Name == name);
        if (index < 0)
        {
            throw new InvalidOperationException($"the object has no member named '{name}'");
        }

        RemoveEntry(container, container.Members.Select(member => new Span(member.NameStart, member.Value.End)).ToList(), index);
    }

    /// <summary>Removes the element at <paramref name="index"/> of the array at <paramref name="arrayPath"/>, with its separator and line.</summary>
    public void RemoveElement(IReadOnlyList<string> arrayPath, int index)
    {
        var container = Locate(arrayPath);
        if (!container.IsArray || index < 0 || index >= container.Elements.Count)
        {
            throw new InvalidOperationException("there is no array element at the given index");
        }

        RemoveEntry(container, container.Elements.Select(element => new Span(element.Start, element.End)).ToList(), index);
    }

    /// <summary>
    /// Removes one entry the way the template Python merge does: an entry followed by a comma goes with the comma,
    /// a comment on that line and the whole line when nothing else is on it; the last entry takes the comma before
    /// it; a lone entry just goes.
    /// </summary>
    private void RemoveEntry(JsoncNode container, IReadOnlyList<Span> entries, int index)
    {
        var entry = entries[index];
        var limit = container.End - 1;
        var next = JsoncText.SkipTrivia(document, entry.End, limit);
        if (next < limit && document[next] == Comma)
        {
            var (start, end) = JsoncText.Tidy(document, entry.Start, JsoncText.AfterLineComment(document, next + 1));
            Apply([new Edit(start, end - start, string.Empty)]);
            return;
        }

        var from = index > 0 ? entries[index - 1].End : entry.Start;
        Apply([new Edit(from, entry.End - from, string.Empty)]);
    }

    private JsoncNode Locate(IReadOnlyList<string> path)
    {
        var node = JsoncTreeParser.Parse(document);
        foreach (var step in path)
        {
            if (node.IsObject)
            {
                node = FindMember(node, step).Value;
            }
            else if (node.IsArray
                && int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < node.Elements.Count)
            {
                node = node.Elements[index];
            }
            else
            {
                throw new InvalidOperationException($"the path step '{step}' does not resolve");
            }
        }

        return node;
    }

    private static JsoncMember FindMember(JsoncNode container, string name)
    {
        if (!container.IsObject)
        {
            throw new InvalidOperationException("the value at the path is not an object");
        }

        return container.Members.FirstOrDefault(member => member.Name == name)
            ?? throw new InvalidOperationException($"the object has no member named '{name}'");
    }

    /// <summary>
    /// Inserts one item at the end of an object or array, or at the start with <paramref name="first"/>. The
    /// layout follows the template Python merge: the item goes on its own line indented like its siblings; a
    /// trailing comma after the last item is kept as a style (the new last item gets one too); a comment on the
    /// last item's line stays with that item; an empty container is expanded onto separate lines.
    /// </summary>
    private void Insert(JsoncNode container, Item item, bool first)
    {
        var closePosition = container.End - 1;
        var openEnd = container.Start + 1;
        var spans = container.IsObject
            ? container.Members.Select(member => new Span(member.NameStart, member.Value.End)).ToList()
            : container.Elements.Select(element => new Span(element.Start, element.End)).ToList();
        var childIndent = spans.Count > 0 && JsoncText.IsBlank(document, JsoncText.LineStart(document, spans[^1].Start), spans[^1].Start)
            ? JsoncText.LineIndent(document, spans[^1].Start)
            : JsoncText.LineIndent(document, container.Start) + indentUnit;
        var rendered = Render(item, childIndent);

        if (spans.Count > 0)
        {
            if (first)
            {
                Apply([new Edit(spans[0].Start, 0, rendered + "," + newline + childIndent)]);
                return;
            }

            var last = spans[^1];
            var next = JsoncText.SkipTrivia(document, last.End, closePosition);
            if (next < closePosition && document[next] == Comma)
            {
                var position = JsoncText.AfterLineComment(document, next + 1);
                Apply([new Edit(position, 0, newline + childIndent + rendered + ",")]);
                return;
            }

            var end = JsoncText.AfterLineComment(document, last.End);
            var between = Encoding.UTF8.GetString(document, last.End, end - last.End);
            Apply([new Edit(last.End, end - last.End, "," + between + newline + childIndent + rendered)]);
            return;
        }

        var baseIndent = JsoncText.LineIndent(document, container.Start);
        var interior = Encoding.UTF8.GetString(document, openEnd, closePosition - openEnd);
        if (interior.Trim().Length == 0)
        {
            Apply([new Edit(openEnd, closePosition - openEnd, newline + childIndent + rendered + newline + baseIndent)]);
            return;
        }

        // Only comments inside: they stay above the new item.
        var closeLine = JsoncText.LineStart(document, closePosition);
        if (closeLine > openEnd && JsoncText.IsBlank(document, closeLine, closePosition))
        {
            Apply([new Edit(closeLine, 0, childIndent + rendered + newline)]);
            return;
        }

        Apply([new Edit(closePosition, 0, newline + childIndent + rendered + newline + baseIndent)]);
    }

    /// <summary>Renders one item laid out for a block whose line starts at <paramref name="indent"/>.</summary>
    private string Render(Item item, string indent)
    {
        var prefix = item.Name is null ? string.Empty : JsoncText.Key(item.Name) + ": ";
        return prefix + JsoncText.Format(item.Value, indent, indentUnit, newline);
    }

    /// <summary>Applies the edits from the end of the document backwards, so earlier offsets stay valid.</summary>
    private void Apply(IReadOnlyList<Edit> edits)
    {
        var updated = document;
        foreach (var candidate in edits.OrderByDescending(edit => edit.Start))
        {
            var insert = Encoding.UTF8.GetBytes(candidate.Insert);
            updated = [.. updated[..candidate.Start], .. insert, .. updated[(candidate.Start + candidate.Length)..]];
        }

        document = updated;
        Changed = true;
    }

    /// <summary>A member (when <see cref="Name"/> is set) or an element to insert.</summary>
    private sealed record Item(string? Name, JsonNode? Value);

    /// <summary>A byte-range replacement.</summary>
    private readonly record struct Edit(int Start, int Length, string Insert);

    /// <summary>The offsets of an entry: where it starts and where its value ends.</summary>
    private readonly record struct Span(int Start, int End);
}
