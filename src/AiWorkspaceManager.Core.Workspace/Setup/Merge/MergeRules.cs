using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// The shared merge data the template ships in <c>.ai/reference/templates/merge-rules.json</c>: the Copilot
/// settings the template always wins on, and the Claude hook commands older templates installed, which a
/// merge removes.
/// </summary>
public sealed record MergeRules(
    IReadOnlyList<string> PinnedSettings,
    IReadOnlyList<string> LegacyHookCommands)
{
    /// <summary>Loads the rules from a template root; null when the file is missing, malformed or not version 1.</summary>
    public static MergeRules? Load(string templateRoot)
    {
        var path = Path.Combine(templateRoot, ".ai", "reference", "templates", "merge-rules.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var root = JsoncMerger.ParseObject(File.ReadAllText(path));
        if (root is null
            || root["version"] is not JsonValue version
            || !version.TryGetValue<int>(out var number)
            || number != 1)
        {
            return null;
        }

        return new MergeRules(
            StringsOf(root, "vscode", "pinnedSettings"),
            StringsOf(root, "claude", "legacyHookCommands"));
    }

    private static List<string> StringsOf(JsonObject root, string section, string key)
    {
        var result = new List<string>();
        if (root[section] is JsonObject block && block[key] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    result.Add(text);
                }
            }
        }

        return result;
    }
}
