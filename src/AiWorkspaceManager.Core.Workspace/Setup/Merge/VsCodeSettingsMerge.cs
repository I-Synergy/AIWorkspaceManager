using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Merges the template's VS Code settings fragment into a project's <c>.vscode/settings.json</c>. Missing keys are
/// added, objects merge recursively, a conflicting value keeps the project's value and is reported, and the
/// template's pinned keys replace the project's value.
/// </summary>
public static class VsCodeSettingsMerge
{
    /// <summary>Merges the fragment into <paramref name="targetText"/>; a null target means the file is missing and is created from the fragment.</summary>
    public static JsoncMergeResult Merge(string? targetText, string fragmentText, IReadOnlySet<string> pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(fragmentText);
        ArgumentNullException.ThrowIfNull(pinnedKeys);

        var template = JsoncMerger.ParseObject(fragmentText);
        if (template is null)
        {
            return JsoncMergeResult.Invalid("the template settings fragment is not a valid JSON object");
        }

        if (targetText is null)
        {
            return JsoncMergeResult.Created(fragmentText, TopLevelKeys(template));
        }

        var target = JsoncMerger.ParseObject(targetText);
        if (target is null)
        {
            return JsoncMergeResult.Invalid("the project's .vscode/settings.json is not a valid JSON object; it was left unchanged");
        }

        var editor = new JsoncMerger(targetText);
        var report = new MergeReport();
        foreach (var pair in template)
        {
            JsonObjectMerge.MergeMember(editor, [], pair.Key, pair.Value, target, pinnedKeys, report);
        }

        return report.Finish(editor);
    }

    internal static IReadOnlyList<string> TopLevelKeys(JsonObject document) => document.Select(pair => pair.Key).ToList();
}
