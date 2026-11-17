using System.Text.Json.Nodes;
using AiWorkspaceManager.Common;
using AiWorkspaceManager.Core.Catalog;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Folder-level links from <c>.ai/skills|agents|instructions</c> into each selected client's folders, the
/// Claude rules generated from <c>.ai/instructions/</c> (see <see cref="ClaudeRuleMirror"/>), and a per-file
/// link for every hook item that Copilot reads from <c>.github/hooks/</c>. Prompts are linked only while
/// <c>.ai/prompts/</c> holds files; otherwise their links are retired. Retired adapters are removed when they
/// are links or empty directories, and real content there is reported as a conflict and never deleted.
/// Disabling a client removes only its links. This is the C# replacement for <c>sync-platforms.py</c>.
/// </summary>
public sealed class PlatformMirrorService
{
    private const string PromptSource = ".ai/prompts";

    private static readonly (string Client, string Source, string Target)[] Links =
    [
        ("claude", ".ai/skills", ".claude/skills"),
        ("copilot", ".ai/skills", ".github/skills"),
        ("agents", ".ai/skills", ".agents/skills"),
        ("claude", ".ai/agents", ".claude/agents"),
        ("copilot", ".ai/agents", ".github/agents"),
        ("copilot", ".ai/instructions", ".github/instructions"),
        ("claude", PromptSource, ".claude/commands"),
        ("copilot", PromptSource, ".github/prompts"),
    ];

    /// <summary>Adapter paths the template retired. Removed when they are links or empty directories, for every client.</summary>
    private static readonly string[] LegacyTargets = [".claude/instructions"];

    private static readonly string[] ClientDirectories = [".claude", ".github", ".agents"];

    /// <summary>
    /// Hook items are <c>.ai/hooks/&lt;name&gt;/hooks.json</c>, but Copilot only loads
    /// <c>.github/hooks/*.json</c> at the top level, so hooks are linked per file rather than as a
    /// folder.
    /// </summary>
    private const string HookSourceRelative = ".ai/hooks";
    private const string CopilotHookTargetRelative = ".github/hooks";

    private readonly WorkspacePaths paths;

    public PlatformMirrorService(WorkspacePaths paths)
    {
        this.paths = paths;
    }

    public MirrorReport Sync(ManifestDocument manifest, bool dryRun = false)
    {
        var clients = SelectionEngine.Clients(manifest);
        var prefix = dryRun ? "[DRY RUN] " : string.Empty;
        var lines = new List<string>
        {
            $"{prefix}Ensuring folder-level links for: "
            + (string.Join(", ", ResolverDefaults.Clients.Where(clients.Contains)) is { Length: > 0 } names
                ? names
                : "(no clients)"),
        };

        var changed = 0;
        var conflicts = new List<string>();
        var promptsPresent = PromptsPresent();
        foreach (var (client, sourceRelative, targetRelative) in Links)
        {
            if (clients.Contains(client) && (sourceRelative != PromptSource || promptsPresent))
            {
                var result = SyncLink(sourceRelative, targetRelative, dryRun);
                if (result == "OK")
                {
                    lines.Add($"  OK       {targetRelative} -> {sourceRelative}");
                }
                else if (result == "SKIP")
                {
                    lines.Add($"  SKIP     {targetRelative} -> {sourceRelative} (source missing)");
                }
                else if (result == "CONFLICT")
                {
                    lines.Add($"  CONFLICT {targetRelative} -> {sourceRelative} "
                        + "(real files; merge into .ai/ and delete the directory)");
                    conflicts.Add(targetRelative);
                }
                else
                {
                    var label = dryRun ? $"WOULD {result}" : result;
                    lines.Add($"  {label} {targetRelative} -> {sourceRelative}");
                    changed++;
                }
            }
            else if (clients.Contains(client))
            {
                // Prompts are not installed, so the client's prompt link is retired rather than created.
                changed += RetireTarget(targetRelative, "no .ai/prompts files", dryRun, lines, conflicts);
            }
            else
            {
                var result = RemoveLink(targetRelative, dryRun);
                if (result == "REMOVED")
                {
                    lines.Add($"  {(dryRun ? "WOULD REMOVE" : "REMOVED")} {targetRelative} ({client} disabled)");
                    changed++;
                }
                else if (result == "KEEP")
                {
                    lines.Add($"  KEEP {targetRelative} (not a link; {client} disabled)");
                }
            }
        }

        foreach (var legacy in LegacyTargets)
        {
            changed += RetireTarget(legacy, "retired adapter", dryRun, lines, conflicts);
        }

        changed += new ClaudeRuleMirror(paths.Root).Sync(clients.Contains("claude"), dryRun, lines, conflicts);

        var removed = PruneEmptyClientDirectories(dryRun);
        foreach (var relative in removed)
        {
            lines.Add($"  {(dryRun ? "WOULD REMOVE" : "REMOVED")} {relative} (empty)");
        }

        changed += SyncHookLinks(clients.Contains("copilot"), dryRun, lines, conflicts);

        if (conflicts.Count > 0)
        {
            lines.Add("\nResolve each CONFLICT by hand: move real files into .ai/ and remove the copy, "
                + "or rename a hand-written file, then sync again.");
        }

        lines.Add($"\n{changed} item(s) {(dryRun ? "would be " : string.Empty)}changed.");
        return new MirrorReport(changed, lines, removed, conflicts);
    }

    private string SyncLink(string sourceRelative, string targetRelative, bool dryRun)
    {
        var target = Path.Combine(paths.Root, targetRelative);
        var source = Path.GetFullPath(Path.Combine(paths.Root, sourceRelative));
        if (!Directory.Exists(source))
        {
            return "SKIP";
        }

        string action;
        if (PathOps.ExistsOrLink(target))
        {
            if (PathOps.IsLink(target) && FirstEntryReachable(target, source))
            {
                return "OK";
            }

            // A real directory holding files is a second copy of the canonical source.
            // Never delete it; report it so the duplicate gets merged by hand.
            if (!PathOps.IsLink(target)
                && Directory.Exists(target)
                && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return "CONFLICT";
            }

            action = "RECREATE";
        }
        else
        {
            action = "CREATE";
        }

        if (dryRun)
        {
            return action;
        }

        if (PathOps.ExistsOrLink(target))
        {
            Materializer.RemovePath(target);
        }

        return Materializer.CreateLink(target, source)
            ? action
            : "FAIL — could not create junction/symlink";
    }

    /// <summary>
    /// Link every <c>.ai/hooks/&lt;name&gt;/hooks.json</c> to <c>.github/hooks/&lt;name&gt;.json</c>.
    /// A link is recognised by content, not by link type: Windows may hardlink or copy the file.
    /// </summary>
    private int SyncHookLinks(bool enabled, bool dryRun, List<string> lines, List<string> conflicts)
    {
        var changed = 0;
        var sourceRoot = Path.Combine(paths.Root, HookSourceRelative);
        var targetRoot = Path.Combine(paths.Root, CopilotHookTargetRelative);
        var expected = new HashSet<string>(StringComparer.Ordinal);

        if (enabled && Directory.Exists(sourceRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                var source = Path.Combine(directory, "hooks.json");
                if (!File.Exists(source))
                {
                    continue;
                }

                var name = Path.GetFileName(directory);
                var target = Path.Combine(targetRoot, name + ".json");
                expected.Add(target);
                var targetRelative = $"{CopilotHookTargetRelative}/{name}.json";
                var result = SyncFileLink(source, target, dryRun);
                switch (result)
                {
                    case "OK":
                        lines.Add($"  OK       {targetRelative} -> {HookSourceRelative}/{name}/hooks.json");
                        break;
                    case "CONFLICT":
                        lines.Add($"  CONFLICT {targetRelative} (real file, differs from {HookSourceRelative}/{name}/hooks.json)");
                        conflicts.Add(targetRelative);
                        break;
                    default:
                        lines.Add($"  {(dryRun ? $"WOULD {result}" : result)} {targetRelative} "
                            + $"-> {HookSourceRelative}/{name}/hooks.json");
                        changed++;
                        break;
                }
            }
        }
        else if (enabled)
        {
            lines.Add($"  SKIP     {CopilotHookTargetRelative}/ (no {HookSourceRelative} items)");
        }

        if (!Directory.Exists(targetRoot))
        {
            return changed;
        }

        // Links this service created for hooks that are no longer installed.
        foreach (var entry in Directory.EnumerateFileSystemEntries(targetRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (expected.Contains(entry))
            {
                continue;
            }

            var relative = $"{CopilotHookTargetRelative}/{Path.GetFileName(entry)}";
            if (!PathOps.IsLink(entry))
            {
                // A hand-written hook file looks the same as a link this service could not
                // create as a link, so it is reported rather than deleted.
                lines.Add($"  KEEP {relative} (not a link; remove it if the hook was uninstalled)");
                continue;
            }

            lines.Add($"  {(dryRun ? "WOULD REMOVE" : "REMOVED")} {relative} (hook not installed)");
            if (!dryRun)
            {
                Materializer.RemovePath(entry);
            }

            changed++;
        }

        if (!Directory.EnumerateFileSystemEntries(targetRoot).Any())
        {
            lines.Add($"  {(dryRun ? "WOULD REMOVE" : "REMOVED")} {CopilotHookTargetRelative} (empty)");
            if (!dryRun)
            {
                try
                {
                    Directory.Delete(targetRoot);
                }
                catch (IOException)
                {
                    // best effort
                }
            }

            changed++;
        }

        return changed;
    }

    private static string SyncFileLink(string source, string target, bool dryRun)
    {
        var action = "CREATE";
        if (PathOps.ExistsOrLink(target))
        {
            if (File.Exists(target)
                && string.Equals(ContentHash.FileSha(target), ContentHash.FileSha(source), StringComparison.Ordinal))
            {
                return "OK";
            }

            if (!PathOps.IsLink(target))
            {
                return "CONFLICT";
            }

            action = "RECREATE";
        }

        if (dryRun)
        {
            return action;
        }

        if (PathOps.ExistsOrLink(target))
        {
            Materializer.RemovePath(target);
        }

        return CreateFileLink(target, source) ? action : "FAIL — could not link";
    }

    /// <summary>
    /// A real symlink, so a later sync can tell its own link from a hand-written file. The
    /// materializer's fallback (hardlink or copy) is used only when symlinks are not permitted.
    /// </summary>
    private static bool CreateFileLink(string target, string source)
    {
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            File.CreateSymbolicLink(target, source);
            return true;
        }
        catch (IOException)
        {
            // fall through
        }
        catch (UnauthorizedAccessException)
        {
            // symlink privilege is missing; a hardlink shares the content just as well
        }
        catch (PlatformNotSupportedException)
        {
            // fall through
        }

        return Materializer.CreateLink(target, source);
    }

    /// <summary>
    /// True when <c>.ai/prompts/</c> exists and holds at least one file. Prompts are a third-party catalog kind; the
    /// template's own prompts are skills now, so a fresh project has none and gets no prompt links.
    /// </summary>
    private bool PromptsPresent()
    {
        var prompts = Path.Combine(paths.Root, PromptSource);
        return Directory.Exists(prompts)
            && Directory.EnumerateFiles(prompts, "*", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// Remove a retired adapter: a link, or an empty directory. Real content is reported as a conflict and left in
    /// place. Returns 1 when something was removed (or would be, in a dry run), otherwise 0.
    /// </summary>
    private int RetireTarget(string targetRelative, string reason, bool dryRun, List<string> lines, List<string> conflicts)
    {
        var target = Path.Combine(paths.Root, targetRelative);
        if (!PathOps.ExistsOrLink(target))
        {
            return 0;
        }

        var label = dryRun ? "WOULD REMOVE" : "REMOVED";
        if (PathOps.IsLink(target))
        {
            lines.Add($"  {label} {targetRelative} ({reason})");
        }
        else if (Directory.Exists(target) && !Directory.EnumerateFileSystemEntries(target).Any())
        {
            lines.Add($"  {label} {targetRelative} (empty; {reason})");
        }
        else
        {
            lines.Add($"  CONFLICT {targetRelative} (real content, not a link; left in place)");
            conflicts.Add(targetRelative);
            return 0;
        }

        if (!dryRun)
        {
            Materializer.RemovePath(target);
        }

        return 1;
    }

    private string RemoveLink(string targetRelative, bool dryRun)
    {
        var target = Path.Combine(paths.Root, targetRelative);
        if (!PathOps.ExistsOrLink(target))
        {
            return "ABSENT";
        }

        if (!PathOps.IsLink(target))
        {
            return "KEEP";
        }

        if (!dryRun)
        {
            Materializer.RemovePath(target);
        }

        return "REMOVED";
    }

    private List<string> PruneEmptyClientDirectories(bool dryRun)
    {
        var removed = new List<string>();
        foreach (var relative in ClientDirectories)
        {
            var path = Path.Combine(paths.Root, relative);
            if (!Directory.Exists(path) || PathOps.IsLink(path))
            {
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                continue;
            }

            if (!dryRun)
            {
                try
                {
                    Directory.Delete(path);
                }
                catch (IOException)
                {
                    continue;
                }
            }

            removed.Add(relative);
        }

        return removed;
    }

    private static bool FirstEntryReachable(string target, string source)
    {
        var first = Directory.EnumerateFileSystemEntries(source).FirstOrDefault();
        return first is not null
            && PathOps.ExistsOrLink(Path.Combine(target, Path.GetFileName(first)));
    }
}
