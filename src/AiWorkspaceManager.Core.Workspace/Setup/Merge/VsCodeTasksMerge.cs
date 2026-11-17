using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Merges the template's <c>tasks.json</c> into a project's <c>.vscode/tasks.json</c>. Tasks merge by
/// <c>label</c>: a missing label is appended and an existing label is kept unchanged. Other top-level keys
/// (such as <c>version</c>) are added only when absent.
/// </summary>
public static class VsCodeTasksMerge
{
    private const string TasksKey = "tasks";

    /// <summary>Merges the fragment into <paramref name="targetText"/>; a null target means the file is missing and is created from the fragment.</summary>
    public static JsoncMergeResult Merge(string? targetText, string fragmentText)
    {
        ArgumentNullException.ThrowIfNull(fragmentText);

        var template = JsoncMerger.ParseObject(fragmentText);
        if (template is null)
        {
            return JsoncMergeResult.Invalid("the template tasks fragment is not a valid JSON object");
        }

        if (targetText is null)
        {
            return JsoncMergeResult.Created(fragmentText, VsCodeSettingsMerge.TopLevelKeys(template));
        }

        var target = JsoncMerger.ParseObject(targetText);
        if (target is null)
        {
            return JsoncMergeResult.Invalid("the project's .vscode/tasks.json is not a valid JSON object; it was left unchanged");
        }

        var editor = new JsoncMerger(targetText);
        var report = new MergeReport();
        foreach (var pair in template)
        {
            if (pair.Key == TasksKey && pair.Value is JsonArray templateTasks)
            {
                MergeTasks(editor, target, templateTasks, report);
            }
            else if (!target.ContainsKey(pair.Key))
            {
                // VS Code files conventionally lead with the schema version.
                editor.AddMember([], pair.Key, pair.Value, first: pair.Key == "version");
                report.Add(pair.Key);
            }
        }

        return report.Finish(editor);
    }

    private static void MergeTasks(JsoncMerger editor, JsonObject target, JsonArray templateTasks, MergeReport report)
    {
        if (!target.ContainsKey(TasksKey))
        {
            editor.AddMember([], TasksKey, templateTasks);
            report.Add(TasksKey);
            return;
        }

        if (target[TasksKey] is not JsonArray currentTasks)
        {
            report.Conflict(TasksKey);
            return;
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in currentTasks)
        {
            var existing = LabelOf(task);
            if (existing is not null)
            {
                labels.Add(existing);
            }
        }

        foreach (var task in templateTasks)
        {
            var label = LabelOf(task);
            if (label is null)
            {
                if (currentTasks.Any(current => JsonNode.DeepEquals(current, task)))
                {
                    continue;
                }
            }
            else if (!labels.Add(label))
            {
                continue;
            }

            editor.AppendElement([TasksKey], task);
            report.Add($"{TasksKey}[{label ?? "unlabelled"}]");
        }
    }

    private static string? LabelOf(JsonNode? task)
    {
        if (task is JsonObject taskObject
            && taskObject["label"] is JsonValue value
            && value.TryGetValue<string>(out var label))
        {
            return label;
        }

        return null;
    }
}
