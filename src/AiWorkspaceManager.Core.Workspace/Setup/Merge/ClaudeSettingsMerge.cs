using System.Globalization;
using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Merges the template's <c>.claude/settings.json</c> into a project's copy. Legacy hook commands are removed,
/// template hook groups are added by command string, <c>permissions</c> lists are unioned, and everything else
/// follows the shared rule (missing keys added; a conflicting value keeps the project's value and is reported).
/// </summary>
public static class ClaudeSettingsMerge
{
    private const string HooksKey = "hooks";
    private const string PermissionsKey = "permissions";

    private static readonly HashSet<string> UnionPermissionKeys = new(StringComparer.Ordinal)
    {
        "allow",
        "deny",
        "additionalDirectories",
    };

    private static readonly IReadOnlySet<string> NoPinnedKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Merges the template into <paramref name="targetText"/>; a null target means the file is missing and is created from the template.</summary>
    public static JsoncMergeResult Merge(string? targetText, string templateText, IReadOnlyList<string> legacyHookCommands)
    {
        ArgumentNullException.ThrowIfNull(templateText);
        ArgumentNullException.ThrowIfNull(legacyHookCommands);

        var template = JsoncMerger.ParseObject(templateText);
        if (template is null)
        {
            return JsoncMergeResult.Invalid("the template .claude/settings.json is not a valid JSON object");
        }

        if (targetText is null)
        {
            return JsoncMergeResult.Created(templateText, VsCodeSettingsMerge.TopLevelKeys(template));
        }

        var target = JsoncMerger.ParseObject(targetText);
        if (target is null)
        {
            return JsoncMergeResult.Invalid("the project's .claude/settings.json is not a valid JSON object; it was left unchanged");
        }

        var editor = new JsoncMerger(targetText);
        var report = new MergeReport();
        var legacy = new HashSet<string>(legacyHookCommands, StringComparer.Ordinal);
        RemoveLegacyHooks(editor, target, legacy);

        // Re-read after the removals so the additions decide against the text that will be written.
        var current = JsoncMerger.ParseObject(editor.Text) ?? target;
        foreach (var pair in template)
        {
            if (pair.Key == HooksKey && pair.Value is JsonObject templateHooks)
            {
                MergeHooks(editor, current, templateHooks, report);
            }
            else if (pair.Key == PermissionsKey && pair.Value is JsonObject templatePermissions)
            {
                MergePermissions(editor, current, templatePermissions, report);
            }
            else
            {
                JsonObjectMerge.MergeMember(editor, [], pair.Key, pair.Value, current, NoPinnedKeys, report);
            }
        }

        return report.Finish(editor);
    }

    /// <summary>Removes every hook whose command is a legacy one; a group left without hooks is removed too.</summary>
    private static void RemoveLegacyHooks(JsoncMerger editor, JsonObject target, HashSet<string> legacy)
    {
        if (legacy.Count == 0 || target[HooksKey] is not JsonObject hooks)
        {
            return;
        }

        foreach (var (eventName, value) in hooks)
        {
            if (value is not JsonArray groups)
            {
                continue;
            }

            // Descending indexes keep the earlier positions valid while edits are applied.
            var removedGroups = 0;
            for (var groupIndex = groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                if (groups[groupIndex] is not JsonObject group || group[HooksKey] is not JsonArray items)
                {
                    continue;
                }

                var legacyIndexes = Enumerable.Range(0, items.Count).Where(index => IsLegacy(items[index], legacy)).ToList();
                if (legacyIndexes.Count == 0)
                {
                    continue;
                }

                if (legacyIndexes.Count == items.Count)
                {
                    editor.RemoveElement([HooksKey, eventName], groupIndex);
                    removedGroups++;
                    continue;
                }

                var groupPath = groupIndex.ToString(CultureInfo.InvariantCulture);
                for (var position = legacyIndexes.Count - 1; position >= 0; position--)
                {
                    editor.RemoveElement([HooksKey, eventName, groupPath, HooksKey], legacyIndexes[position]);
                }
            }

            // An event left with no groups goes too, so no empty "Event": [] is left behind.
            if (removedGroups > 0 && removedGroups == groups.Count)
            {
                editor.RemoveMember([HooksKey], eventName);
            }
        }
    }

    private static void MergeHooks(JsoncMerger editor, JsonObject current, JsonObject templateHooks, MergeReport report)
    {
        if (!current.ContainsKey(HooksKey))
        {
            editor.AddMember([], HooksKey, templateHooks);
            report.Add(HooksKey);
            return;
        }

        if (current[HooksKey] is not JsonObject currentHooks)
        {
            report.Conflict(HooksKey);
            return;
        }

        foreach (var (eventName, templateEvent) in templateHooks)
        {
            if (templateEvent is not JsonArray templateGroups)
            {
                JsonObjectMerge.MergeMember(editor, [HooksKey], eventName, templateEvent, currentHooks, NoPinnedKeys, report);
                continue;
            }

            var eventKey = HooksKey + "." + eventName;
            if (!currentHooks.ContainsKey(eventName))
            {
                editor.AddMember([HooksKey], eventName, templateGroups);
                report.Add(eventKey);
                continue;
            }

            if (currentHooks[eventName] is not JsonArray currentGroups)
            {
                report.Conflict(eventKey);
                continue;
            }

            var known = currentGroups.SelectMany(HookKeysOf).ToHashSet(StringComparer.Ordinal);
            foreach (var templateGroup in templateGroups.OfType<JsonObject>())
            {
                if (templateGroup[HooksKey] is not JsonArray templateHookList)
                {
                    continue;
                }

                var missing = new List<JsonNode?>();
                foreach (var hook in templateHookList)
                {
                    if (known.Add(HookKey(hook)))
                    {
                        missing.Add(hook);
                    }
                }

                if (missing.Count == 0)
                {
                    continue;
                }

                editor.AppendElement([HooksKey, eventName], GroupWithHooks(templateGroup, missing));
                report.Add(eventKey);
            }
        }
    }

    private static void MergePermissions(JsoncMerger editor, JsonObject current, JsonObject templatePermissions, MergeReport report)
    {
        if (!current.ContainsKey(PermissionsKey))
        {
            editor.AddMember([], PermissionsKey, templatePermissions);
            report.Add(PermissionsKey);
            return;
        }

        if (current[PermissionsKey] is not JsonObject currentPermissions)
        {
            report.Conflict(PermissionsKey);
            return;
        }

        foreach (var (key, value) in templatePermissions)
        {
            if (value is not JsonArray templateList || !UnionPermissionKeys.Contains(key))
            {
                JsonObjectMerge.MergeMember(editor, [PermissionsKey], key, value, currentPermissions, NoPinnedKeys, report);
                continue;
            }

            if (!currentPermissions.ContainsKey(key))
            {
                editor.AddMember([PermissionsKey], key, templateList);
                report.Add(PermissionsKey + "." + key);
                continue;
            }

            if (currentPermissions[key] is not JsonArray currentList)
            {
                report.Conflict(PermissionsKey + "." + key);
                continue;
            }

            var known = new List<JsonNode?>(currentList);
            var appended = false;
            foreach (var item in templateList)
            {
                if (known.Any(existing => JsonNode.DeepEquals(existing, item)))
                {
                    continue;
                }

                editor.AppendElement([PermissionsKey, key], item);
                known.Add(item);
                appended = true;
            }

            if (appended)
            {
                report.Add(PermissionsKey + "." + key);
            }
        }
    }

    private static bool IsLegacy(JsonNode? hook, HashSet<string> legacy) =>
        hook is JsonObject hookObject
        && hookObject["command"] is JsonValue value
        && value.TryGetValue<string>(out var command)
        && legacy.Contains(command);

    /// <summary>The identity of a hook: its command string, or its JSON when it has none.</summary>
    private static string HookKey(JsonNode? hook)
    {
        if (hook is JsonObject hookObject
            && hookObject["command"] is JsonValue value
            && value.TryGetValue<string>(out var command))
        {
            return command;
        }

        return hook?.ToJsonString() ?? "null";
    }

    private static IEnumerable<string> HookKeysOf(JsonNode? group) =>
        group is JsonObject groupObject && groupObject[HooksKey] is JsonArray hooks
            ? hooks.Select(HookKey)
            : [];

    /// <summary>A copy of a template hook group that carries only the hooks still missing from the project.</summary>
    private static JsonObject GroupWithHooks(JsonObject templateGroup, IReadOnlyList<JsonNode?> hooks)
    {
        var group = new JsonObject();
        foreach (var pair in templateGroup)
        {
            if (pair.Key == HooksKey)
            {
                group[HooksKey] = new JsonArray(hooks.Select(hook => hook?.DeepClone()).ToArray());
            }
            else
            {
                group[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return group;
    }
}
