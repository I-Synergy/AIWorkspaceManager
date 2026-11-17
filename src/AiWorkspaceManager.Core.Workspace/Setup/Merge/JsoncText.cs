using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>Byte-level helpers for the text-surgery merger: trivia, lines, indentation and value rendering.</summary>
internal static class JsoncText
{
    private const byte Space = (byte)' ';
    private const byte Tab = (byte)'\t';
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';
    private const byte Slash = (byte)'/';
    private const byte Star = (byte)'*';

    /// <summary>Skips whitespace and comments starting at <paramref name="position"/>; returns the first other byte (or <paramref name="limit"/>).</summary>
    public static int SkipTrivia(byte[] bytes, int position, int limit)
    {
        var index = position;
        while (index < limit)
        {
            var current = bytes[index];
            if (current is Space or Tab or CarriageReturn or LineFeed)
            {
                index++;
            }
            else if (current == Slash && index + 1 < limit && bytes[index + 1] == Slash)
            {
                while (index < limit && bytes[index] != LineFeed)
                {
                    index++;
                }
            }
            else if (current == Slash && index + 1 < limit && bytes[index + 1] == Star)
            {
                var close = index + 2;
                while (close + 1 < limit && !(bytes[close] == Star && bytes[close + 1] == Slash))
                {
                    close++;
                }

                index = close + 1 < limit ? close + 2 : limit;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>Offset of the first byte of the line containing <paramref name="position"/>.</summary>
    public static int LineStart(byte[] bytes, int position)
    {
        var index = position;
        while (index > 0 && bytes[index - 1] != LineFeed)
        {
            index--;
        }

        return index;
    }

    /// <summary>The leading spaces and tabs of the line containing <paramref name="position"/>.</summary>
    public static string LineIndent(byte[] bytes, int position)
    {
        var start = LineStart(bytes, position);
        var end = start;
        while (end < bytes.Length && (bytes[end] is Space or Tab))
        {
            end++;
        }

        return Encoding.UTF8.GetString(bytes, start, end - start);
    }

    /// <summary>True when every byte in [from, to) is a space, a tab or a carriage return.</summary>
    public static bool IsBlank(byte[] bytes, int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            if (bytes[index] is not (Space or Tab or CarriageReturn))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when [from, to) contains a line feed.</summary>
    public static bool HasNewline(byte[] bytes, int from, int to)
    {
        for (var index = from; index < to; index++)
        {
            if (bytes[index] == LineFeed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When only blanks separate <paramref name="position"/> from the end of its line, returns the offset just
    /// past that line break (or <paramref name="limit"/> at the end of the document); otherwise -1.
    /// </summary>
    public static int EndOfLineAfter(byte[] bytes, int position, int limit)
    {
        var index = position;
        while (index < limit && (bytes[index] is Space or Tab or CarriageReturn))
        {
            index++;
        }

        if (index >= limit)
        {
            return limit;
        }

        return bytes[index] == LineFeed ? index + 1 : -1;
    }

    /// <summary>The document's line break: CRLF when the file uses it, otherwise LF.</summary>
    public static string DetectNewline(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// The indentation unit, as the template's Python merge detects it: a tab when any line starts with one,
    /// otherwise the narrowest run of leading spaces on a line with content, otherwise four spaces.
    /// </summary>
    public static string DetectIndent(string text)
    {
        var narrowest = int.MaxValue;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                return "\t";
            }

            var spaces = line.Length - line.TrimStart(' ').Length;
            if (spaces > 0 && spaces < line.Length && !char.IsWhiteSpace(line[spaces]))
            {
                narrowest = Math.Min(narrowest, spaces);
            }
        }

        return narrowest == int.MaxValue ? "    " : new string(' ', narrowest);
    }

    /// <summary>
    /// When only a same-line comment follows <paramref name="position"/>, returns the offset of the end of that
    /// line (before a carriage return); otherwise <paramref name="position"/>. Keeps a trailing comment with its entry.
    /// </summary>
    public static int AfterLineComment(byte[] bytes, int position)
    {
        var lineEnd = Array.IndexOf(bytes, LineFeed, position);
        if (lineEnd < 0)
        {
            return position;
        }

        var rest = Encoding.UTF8.GetString(bytes, position, lineEnd - position).Trim();
        var isLine = rest.StartsWith("//", StringComparison.Ordinal);
        var isBlock = rest.StartsWith("/*", StringComparison.Ordinal)
            && rest.EndsWith("*/", StringComparison.Ordinal)
            && rest.Split("*/").Length == 2;
        if (!isLine && !isBlock)
        {
            return position;
        }

        return bytes[lineEnd - 1] == CarriageReturn ? lineEnd - 1 : lineEnd;
    }

    /// <summary>
    /// The span to delete for an entry that carries a trailing comma: widened over its whole line when the line
    /// holds nothing else.
    /// </summary>
    public static (int Start, int End) Tidy(byte[] bytes, int start, int end)
    {
        var cursor = end;
        while (cursor < bytes.Length && bytes[cursor] is Space or Tab)
        {
            cursor++;
        }

        // Other content shares the line: the blanks after the span go with it so the next entry keeps its place.
        var lineStart = LineStart(bytes, start);
        for (var index = lineStart; index < start; index++)
        {
            if (bytes[index] is not (Space or Tab))
            {
                return (start, cursor);
            }
        }

        if (cursor + 1 < bytes.Length && bytes[cursor] == CarriageReturn && bytes[cursor + 1] == LineFeed)
        {
            return (lineStart, cursor + 2);
        }

        if (cursor < bytes.Length && bytes[cursor] == LineFeed)
        {
            return (lineStart, cursor + 1);
        }

        return cursor >= bytes.Length ? (lineStart, cursor) : (start, cursor);
    }

    /// <summary>A member name as a quoted, escaped JSON string.</summary>
    public static string Key(string name) => JsonSerializer.Serialize(name, JsonFiles.Compact);

    /// <summary>A value on one line.</summary>
    public static string Compact(JsonNode? node) => node is null ? "null" : node.ToJsonString(JsonFiles.Compact);

    /// <summary>
    /// A value laid out for a block whose line starts at <paramref name="indent"/>: non-empty objects and arrays
    /// get one member or element per line, indented by <paramref name="unit"/>.
    /// </summary>
    public static string Format(JsonNode? node, string indent, string unit, string newline)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject { Count: 0 }:
                return "{}";
            case JsonObject jsonObject:
                {
                    var inner = indent + unit;
                    var members = jsonObject.Select(pair => inner + Key(pair.Key) + ": " + Format(pair.Value, inner, unit, newline));
                    return "{" + newline + string.Join("," + newline, members) + newline + indent + "}";
                }
            case JsonArray { Count: 0 }:
                return "[]";
            case JsonArray jsonArray:
                {
                    var inner = indent + unit;
                    var elements = jsonArray.Select(element => inner + Format(element, inner, unit, newline));
                    return "[" + newline + string.Join("," + newline, elements) + newline + indent + "]";
                }
            default:
                return Compact(node);
        }
    }
}
