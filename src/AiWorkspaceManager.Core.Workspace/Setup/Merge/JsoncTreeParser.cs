using System.Text.Json;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Builds the span tree of a JSON(C) document with <see cref="Utf8JsonReader"/>, skipping comments and
/// allowing trailing commas. Only offsets are recorded; the text itself is never re-serialised.
/// </summary>
internal static class JsoncTreeParser
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses one document. Throws <see cref="JsonException"/> when the text is not valid JSON(C).</summary>
    public static JsoncNode Parse(byte[] utf8)
    {
        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        Advance(ref reader);
        var root = ParseValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("unexpected content after the root value");
        }

        return root;
    }

    private static JsoncNode ParseValue(ref Utf8JsonReader reader)
    {
        var start = (int)reader.TokenStartIndex;
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ParseObject(ref reader, start);
            case JsonTokenType.StartArray:
                return ParseArray(ref reader, start);
            default:
                return new JsoncNode(isObject: false, isArray: false, start) { End = (int)reader.BytesConsumed };
        }
    }

    private static JsoncNode ParseObject(ref Utf8JsonReader reader, int start)
    {
        var node = new JsoncNode(isObject: true, isArray: false, start);
        while (true)
        {
            Advance(ref reader);
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                node.End = (int)reader.TokenStartIndex + 1;
                return node;
            }

            var nameStart = (int)reader.TokenStartIndex;
            var name = reader.GetString() ?? string.Empty;
            if (node.Members.Any(existing => existing.Name == name))
            {
                // Writing into a file whose meaning is ambiguous is unsafe, so a repeated key is invalid.
                throw new JsonException($"duplicate key '{name}'");
            }

            Advance(ref reader);
            node.Members.Add(new JsoncMember(name, nameStart, ParseValue(ref reader)));
        }
    }

    private static JsoncNode ParseArray(ref Utf8JsonReader reader, int start)
    {
        var node = new JsoncNode(isObject: false, isArray: true, start);
        while (true)
        {
            Advance(ref reader);
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                node.End = (int)reader.TokenStartIndex + 1;
                return node;
            }

            node.Elements.Add(ParseValue(ref reader));
        }
    }

    private static void Advance(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("unexpected end of the document");
        }
    }
}
