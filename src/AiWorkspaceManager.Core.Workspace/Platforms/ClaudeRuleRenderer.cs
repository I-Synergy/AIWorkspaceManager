using System.Text.RegularExpressions;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Renders one <c>.ai/instructions/{name}.instructions.md</c> source into the Claude Code rule text
/// written to <c>.claude/rules/{name}.instructions.md</c>. The output must stay byte-identical to
/// <c>render_rule</c> in <c>.ai/scripts/sync-platforms.py</c>, because both tools write the same file.
/// </summary>
public static class ClaudeRuleRenderer
{
    /// <summary>The canonical source folder the rules are generated from.</summary>
    public const string SourceDirectory = ".ai/instructions";

    /// <summary>The suffix every instruction source and generated rule carries.</summary>
    public const string Suffix = ".instructions.md";

    private static readonly Regex FieldLine = new(
        @"^([A-Za-z_][\w-]*)\s*:\s*(.*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>The marker comment that identifies a file as generated (and so safe to overwrite or remove).</summary>
    public static string Marker(string name) =>
        $"<!-- Generated from {SourceDirectory}/{name}{Suffix} by sync-platforms.py. Edit the source, not this file. -->";

    /// <summary>
    /// Render a rule: the <c>description</c> is kept, <c>applyTo</c> becomes a <c>paths:</c> list (omitted for
    /// <c>**</c> and <c>**/*</c>), every other key is dropped and the body is copied verbatim with LF endings.
    /// </summary>
    public static string Render(string name, string sourceText)
    {
        var (fields, body) = SplitFrontmatter(sourceText);
        var head = new List<string>();
        var description = Value(fields, "description").Trim();
        if (description.Length > 0)
        {
            head.Add($"description: {YamlString(description)}");
        }

        var paths = ClaudePaths(Value(fields, "applyTo"));
        if (paths.Count > 0)
        {
            head.Add("paths:");
            head.AddRange(paths.Select(path => $"  - {YamlString(path)}"));
        }

        var lines = new List<string>();
        if (head.Count > 0)
        {
            lines.Add("---");
            lines.AddRange(head);
            lines.Add("---");
        }

        lines.Add(Marker(name));
        var text = string.Join("\n", lines) + "\n" + body;
        return text.EndsWith('\n') ? text : text + "\n";
    }

    private static string Value(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>Return the frontmatter fields and the body (everything after the closing <c>---</c>).</summary>
    private static (Dictionary<string, string> Fields, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Split('\n');
        if (lines[0].Trim() != "---")
        {
            return ([], text);
        }

        for (var index = 1; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                return (ParseFields(lines[1..index]), string.Join("\n", lines[(index + 1)..]));
            }
        }

        // Unterminated frontmatter: the whole file is body.
        return ([], text);
    }

    /// <summary>Parse top-level <c>key: value</c> lines; folded and literal block scalars are joined onto one line.</summary>
    private static Dictionary<string, string> ParseFields(string[] lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                if (key is not null)
                {
                    fields[key] = (fields[key] + " " + line.Trim()).Trim();
                }

                continue;
            }

            var match = FieldLine.Match(line);
            if (!match.Success)
            {
                key = null;
                continue;
            }

            key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            fields[key] = value is ">" or "|" or ">-" or "|-" ? string.Empty : Unquote(value);
        }

        return fields;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    /// <summary>
    /// Map a Copilot <c>applyTo</c> value to Claude <c>paths:</c> globs. An empty list means an unconditional
    /// rule, which is the case for an empty value or one that matches everything.
    /// </summary>
    private static List<string> ClaudePaths(string applyTo)
    {
        var patterns = applyTo.Split(',')
            .Select(pattern => pattern.Trim().Trim('"', '\'').Trim())
            .Where(pattern => pattern.Length > 0)
            .ToList();
        if (patterns.Count == 0 || patterns.Any(pattern => pattern is "**" or "**/*"))
        {
            return [];
        }

        return patterns;
    }

    /// <summary>A double-quoted YAML scalar.</summary>
    private static string YamlString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
