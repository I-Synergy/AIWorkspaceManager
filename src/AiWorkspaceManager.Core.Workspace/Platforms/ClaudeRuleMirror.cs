using System.Text;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Generates <c>.claude/rules/*.instructions.md</c> as regular files from <c>.ai/instructions/</c>. Copy-only:
/// it never writes through a link, never overwrites a hand-written file and removes only generated files
/// (those carrying the marker) whose source is gone or whose client is disabled. The C# counterpart of
/// <c>sync_rules</c> in <c>sync-platforms.py</c>.
/// </summary>
internal sealed class ClaudeRuleMirror
{
    private const string RulesRelative = ".claude/rules";

    private readonly string root;

    public ClaudeRuleMirror(string root)
    {
        this.root = root;
    }

    /// <summary>
    /// Write or refresh the generated rules when Claude is enabled, and remove every generated rule when it is not.
    /// Returns how many files were written or removed.
    /// </summary>
    public int Sync(bool enabled, bool dryRun, List<string> lines, List<string> conflicts)
    {
        var rulesDir = Path.Combine(root, ".claude", "rules");
        if (PathOps.IsLink(Path.Combine(root, ".claude")) || PathOps.IsLink(rulesDir))
        {
            // The generator never follows links. A disabled client leaves a link alone without a report.
            if (enabled)
            {
                lines.Add($"  CONFLICT {RulesRelative} (a link; the rules generator never follows links)");
                conflicts.Add(RulesRelative);
            }

            return 0;
        }

        if (File.Exists(rulesDir))
        {
            if (enabled)
            {
                lines.Add($"  CONFLICT {RulesRelative} (a file, not a directory)");
                conflicts.Add(RulesRelative);
            }

            return 0;
        }

        var changed = 0;
        var sources = enabled ? SourceFiles() : new List<string>();
        var names = sources.Select(source => NameOf(source)).ToHashSet(StringComparer.Ordinal);
        if (enabled)
        {
            foreach (var source in sources)
            {
                changed += WriteRule(source, rulesDir, dryRun, lines, conflicts);
            }
        }

        changed += RemoveStale(names, enabled, rulesDir, dryRun, lines);
        return changed;
    }

    private static int WriteRule(string source, string rulesDir, bool dryRun, List<string> lines, List<string> conflicts)
    {
        var name = NameOf(source);
        var relative = $"{RulesRelative}/{name}{ClaudeRuleRenderer.Suffix}";
        var target = Path.Combine(rulesDir, name + ClaudeRuleRenderer.Suffix);
        if (PathOps.IsLink(target) || Directory.Exists(target))
        {
            lines.Add($"  CONFLICT {relative} (link or directory at a generated path; not touched)");
            conflicts.Add(relative);
            return 0;
        }

        var current = File.Exists(target) ? ReadText(target) : null;
        if (current is not null && !HasMarker(current, name))
        {
            lines.Add($"  CONFLICT {relative} (hand-written file with the same name; not overwritten)");
            conflicts.Add(relative);
            return 0;
        }

        var desired = ClaudeRuleRenderer.Render(name, ReadText(source));
        if (current == desired)
        {
            lines.Add($"  OK       {relative}");
            return 0;
        }

        var label = dryRun ? "WOULD WRITE" : "WROTE";
        if (!dryRun)
        {
            try
            {
                Directory.CreateDirectory(rulesDir);
                File.WriteAllBytes(target, Encoding.UTF8.GetBytes(desired));
            }
            catch (IOException exception)
            {
                lines.Add($"  FAIL     {relative}: {exception.Message}");
                return 0;
            }
            catch (UnauthorizedAccessException exception)
            {
                lines.Add($"  FAIL     {relative}: {exception.Message}");
                return 0;
            }
        }

        lines.Add($"  {label} {relative} (from {ClaudeRuleRenderer.SourceDirectory}/{Path.GetFileName(source)})");
        return 1;
    }

    /// <summary>Remove generated copies whose source is gone, or every generated copy when Claude is disabled.</summary>
    private static int RemoveStale(
        HashSet<string> names,
        bool enabled,
        string rulesDir,
        bool dryRun,
        List<string> lines)
    {
        if (!Directory.Exists(rulesDir))
        {
            return 0;
        }

        var changed = 0;
        var reason = enabled ? "source removed" : "claude disabled";
        foreach (var stale in Directory.EnumerateFiles(rulesDir, "*" + ClaudeRuleRenderer.Suffix)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = NameOf(stale);
            var relative = $"{RulesRelative}/{Path.GetFileName(stale)}";
            if (names.Contains(name) || PathOps.IsLink(stale))
            {
                continue;
            }

            if (!HasMarker(ReadText(stale), name))
            {
                continue;
            }

            lines.Add($"  {(dryRun ? "WOULD REMOVE" : "REMOVED")} {relative} ({reason})");
            if (!dryRun)
            {
                try
                {
                    File.Delete(stale);
                }
                catch (IOException exception)
                {
                    lines.Add($"  FAIL     {relative}: {exception.Message}");
                    continue;
                }
            }

            changed++;
        }

        return changed;
    }

    private List<string> SourceFiles()
    {
        var sourcesDir = Path.Combine(root, ClaudeRuleRenderer.SourceDirectory);
        if (!Directory.Exists(sourcesDir))
        {
            return [];
        }

        return Directory.EnumerateFiles(sourcesDir, "*" + ClaudeRuleRenderer.Suffix)
            .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToList();
    }

    private static string NameOf(string path)
    {
        var file = Path.GetFileName(path);
        return file[..^ClaudeRuleRenderer.Suffix.Length];
    }

    private static bool HasMarker(string text, string name) =>
        text.Split('\n').Contains(ClaudeRuleRenderer.Marker(name), StringComparer.Ordinal);

    /// <summary>UTF-8 text with LF line endings, matching how the template compares and copies rules.</summary>
    private static string ReadText(string path) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n", StringComparison.Ordinal);
}
