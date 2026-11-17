using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// The shared merge rule for one member of a settings object: a missing key is added, two objects merge
/// recursively, a pinned top-level key takes the template value, equal values do nothing, and any other
/// difference keeps the project's value and is reported as a conflict.
/// </summary>
internal static class JsonObjectMerge
{
    /// <summary>Merges one template member into the project object <paramref name="target"/> (a snapshot of the current text).</summary>
    public static void MergeMember(
        JsoncMerger editor,
        IReadOnlyList<string> path,
        string key,
        JsonNode? template,
        JsonObject target,
        IReadOnlySet<string> pinnedKeys,
        MergeReport report)
    {
        if (!target.ContainsKey(key))
        {
            editor.AddMember(path, key, template);
            report.Add(Dotted(path, key));
            return;
        }

        var current = target[key];
        if (JsonNode.DeepEquals(template, current))
        {
            return;
        }

        if (path.Count == 0 && pinnedKeys.Contains(key))
        {
            if (editor.ReplaceMember(path, key, template))
            {
                report.Pin(key);
            }

            return;
        }

        if (template is JsonObject templateObject && current is JsonObject currentObject)
        {
            IReadOnlyList<string> nested = [.. path, key];
            foreach (var pair in templateObject)
            {
                MergeMember(editor, nested, pair.Key, pair.Value, currentObject, pinnedKeys, report);
            }

            return;
        }

        report.Conflict(Dotted(path, key));
    }

    /// <summary>The dotted name of a member, for the report.</summary>
    public static string Dotted(IReadOnlyList<string> path, string key) => string.Join('.', [.. path, key]);
}
