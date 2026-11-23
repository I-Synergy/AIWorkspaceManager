using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiWorkspaceManager.Common;
using AiWorkspaceManager.Core.Catalog;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Keeps <c>AGENTS.md</c> and the generated Copilot instructions in step with what is installed.
/// <c>AGENTS.md</c> is the source of truth: references to missing items are filtered out, the
/// installed set is listed, and the Copilot file is regenerated from it plus its Copilot-only
/// sections. This replaces the Python resolver's <c>update_instruction_docs</c>.
/// </summary>
public sealed partial class InstructionDocumentService
{
    /// <summary>Upper bound for every regex here; the inputs are small, so this only guards against runaways.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The same bound for <see cref="GeneratedRegexAttribute"/>, which takes milliseconds.</summary>
    private const int RegexTimeoutMs = 1000;

    private const string AgentsAnchor = ".ai/session-context.md";
    private const string CopilotFile = ".github/copilot-instructions.md";
    private const string MarkerPrefix = "<!-- ai-workspace-manager:";
    private const string CopilotGenerated = MarkerPrefix + "generated-from-agents -->";
    private const string CopilotReferenceStart = MarkerPrefix + "copilot-reference:start -->";
    private const string CopilotReferenceEnd = MarkerPrefix + "copilot-reference:end -->";
    private const string CopilotRuntimeStart = MarkerPrefix + "copilot-runtime:start -->";
    private const string CopilotRuntimeEnd = MarkerPrefix + "copilot-runtime:end -->";
    private const string CopilotRuntimeFile = ".ai/reference/copilot-runtime.md";
    private const string CopilotNote =
        "<!-- Generated from AGENTS.md. Edit AGENTS.md, not this file. "
        + "Regenerate with `python .ai/scripts/sync-instructions.py`. -->";
    private const string InstalledStart = MarkerPrefix + "installed:start -->";
    private const string InstalledEnd = MarkerPrefix + "installed:end -->";
    private const string LegacyMarkerPrefix = "<!-- ai-manager:";

    private static readonly string[] CopilotReferenceSections =
    [
        "Project Context",
        "Architecture",
        "Forbidden Technologies",
        "Patterns & Templates",
        "Naming Conventions",
        "Token Replacements",
        "Core Operational Rules",
        "Work-Type Context Mapping",
    ];

    private readonly WorkspaceQueries queries;

    public InstructionDocumentService(WorkspaceQueries queries)
    {
        this.queries = queries;
    }

    public Dictionary<string, string> Update(JsonObject? manifestRoot)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifestRoot is null)
        {
            return results;
        }

        var manifest = ManifestDocument.Temporary(manifestRoot);
        if (TemplateIsThisWorkspace(manifest))
        {
            return results;
        }

        var catalog = ReadInstalled();
        var names = new InstalledNames(
            catalog.Skills.Select(item => item.Name).ToHashSet(StringComparer.Ordinal),
            catalog.Agents.Select(item => item.Name).ToHashSet(StringComparer.Ordinal));
        var section = InstalledSection(catalog);

        var agentsPath = Path.Combine(queries.Paths.Root, "AGENTS.md");
        var agentsRaw = File.Exists(agentsPath) ? File.ReadAllText(agentsPath) : string.Empty;
        var agentsText = UpdateAgentsDocument(agentsPath, agentsRaw, names, section, results);

        var copilotPath = Path.Combine(queries.Paths.Root, CopilotFile);
        if (!SelectionEngine.Clients(manifest).Contains("copilot"))
        {
            RemoveCopilotDocument(copilotPath, results);
            return results;
        }

        if (agentsText.Length > 0)
        {
            WriteCopilotDocument(this, copilotPath, agentsText, names, manifest, results);
        }

        return results;
    }

    /// <summary>A template source that resolves to this workspace is the project itself: nothing to merge.</summary>
    private bool TemplateIsThisWorkspace(ManifestDocument manifest)
    {
        var entry = SourceCatalog.EntryNamed(manifest, ResolverDefaults.TemplateSource);
        if (SourceCatalog.IsRemote(entry))
        {
            return false;
        }

        var sourceRoot = queries.Sources.SourceRoot(ResolverDefaults.TemplateSource, entry);
        return WorkspacePaths.SamePath(sourceRoot, queries.Paths.Root);
    }

    /// <summary>Rewrites the managed block and the item references in AGENTS.md; returns the new text.</summary>
    private static string UpdateAgentsDocument(
        string agentsPath,
        string agentsRaw,
        InstalledNames names,
        string section,
        Dictionary<string, string> results)
    {
        var agentsText = NormalizeMarkers(agentsRaw);
        if (agentsText.Length == 0)
        {
            return agentsText;
        }

        if (agentsText.Contains(AgentsAnchor, StringComparison.Ordinal))
        {
            agentsText = FilterItemRefs(agentsText, names);
            agentsText = InstalledBlock().IsMatch(agentsText)
                ? InstalledBlock().Replace(agentsText, section).TrimEnd() + "\n"
                : agentsText.TrimEnd() + "\n\n" + section + "\n";
        }

        if (agentsText == agentsRaw)
        {
            results["AGENTS.md"] = "unchanged";
            return agentsText;
        }

        File.WriteAllText(agentsPath, agentsText);
        results["AGENTS.md"] = "updated";
        return agentsText;
    }

    /// <summary>Deletes the generated Copilot instructions, and their folder when nothing else is left.</summary>
    private static void RemoveCopilotDocument(string copilotPath, Dictionary<string, string> results)
    {
        if (!File.Exists(copilotPath))
        {
            return;
        }

        var existing = File.ReadAllText(copilotPath);
        if (!existing.Contains(CopilotGenerated, StringComparison.Ordinal)
            && !existing.Contains(LegacyMarkerPrefix, StringComparison.Ordinal))
        {
            results[CopilotFile] = "kept";
            return;
        }

        File.Delete(copilotPath);
        var parent = Path.GetDirectoryName(copilotPath);
        if (!string.IsNullOrEmpty(parent)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }

        results[CopilotFile] = "removed";
    }

    private static void WriteCopilotDocument(
        InstructionDocumentService service,
        string copilotPath,
        string agentsText,
        InstalledNames names,
        ManifestDocument manifest,
        Dictionary<string, string> results)
    {
        var existing = File.Exists(copilotPath) ? File.ReadAllText(copilotPath) : null;
        var document = CopilotDocument(agentsText, existing, names, service.ReadCopilotRuntime(manifest));
        if (document is null)
        {
            results[CopilotFile] = "kept";
            return;
        }

        if (document == existing)
        {
            results[CopilotFile] = "unchanged";
            return;
        }

        var parent = Path.GetDirectoryName(copilotPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(copilotPath, document);
        results[CopilotFile] = "updated";
    }

    public IReadOnlyList<(string Name, string Description)> InstalledSkills()
    {
        var skills = new List<(string Name, string Description)>();
        var directory = Path.Combine(queries.Paths.AiDirectory, "skills");
        if (!Directory.Exists(directory))
        {
            return skills;
        }

        foreach (var skillDirectory in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var document = Path.Combine(skillDirectory, "SKILL.md");
            if (!File.Exists(document))
            {
                continue;
            }

            var meta = Frontmatter.Parse(File.ReadAllText(document));
            skills.Add((
                meta.GetString("name") ?? Path.GetFileName(skillDirectory),
                FirstLine(meta.GetString("description"))));
        }

        return skills;
    }

    public IReadOnlyList<(string Name, string Description)> InstalledAgents()
    {
        var agents = new List<(string Name, string Description)>();
        var directory = Path.Combine(queries.Paths.AiDirectory, "agents");
        if (!Directory.Exists(directory))
        {
            return agents;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.md").OrderBy(path => path, StringComparer.Ordinal))
        {
            var meta = Frontmatter.Parse(File.ReadAllText(file));
            agents.Add((
                meta.GetString("name") ?? Path.GetFileNameWithoutExtension(file),
                FirstLine(meta.GetString("description"))));
        }

        return agents;
    }

    private InstalledCatalog ReadInstalled() =>
        new(InstalledSkills(), InstalledAgents());

    private static string InstalledSection(InstalledCatalog catalog)
    {
        var lines = new List<string> { "## Installed skills and agents", string.Empty, InstalledStart };
        foreach (var (kind, title) in new[] { ("skills", "Skills"), ("agents", "Agents") })
        {
            var items = kind == "skills" ? catalog.Skills : catalog.Agents;
            if (items.Count == 0)
            {
                continue;
            }

            lines.Add($"### {title}");
            foreach (var (name, description) in items)
            {
                lines.Add(string.IsNullOrEmpty(description)
                    ? $"- `{name}`"
                    : $"- `{name}` — {description}");
            }

            lines.Add(string.Empty);
        }

        lines.Add(InstalledEnd);
        return string.Join("\n", lines);
    }

    private static string FilterItemRefs(string text, InstalledNames names)
    {
        var kept = new List<string>();
        foreach (var line in text.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            var remaining = FilterLine(line, names, out var missing);
            if (!missing)
            {
                kept.Add(line);
                continue;
            }

            if (ShouldDropLine(remaining))
            {
                continue;
            }

            kept.Add(remaining);
        }

        var result = string.Join("\n", kept);
        return text.EndsWith('\n') ? result + "\n" : result;
    }

    /// <summary>Drops references to items that are no longer installed, then tidies what is left.</summary>
    private static string FilterLine(string line, InstalledNames names, out bool missing)
    {
        var found = false;
        var updated = ItemRef().Replace(line, match =>
        {
            if (IsInstalledReference(match, names))
            {
                return match.Value;
            }

            found = true;
            return string.Empty;
        });

        missing = found;
        return found ? TidyReferenceLine(updated) : updated;
    }

    private static bool IsInstalledReference(Match match, InstalledNames names)
    {
        var skill = match.Groups["skill"].Value;
        return skill.Length > 0
            ? names.Skills.Contains(skill)
            : names.Agents.Contains(match.Groups["agent"].Value);
    }

    /// <summary>A table row whose only content was the removed reference goes away with it.</summary>
    private static bool ShouldDropLine(string line) =>
        string.IsNullOrWhiteSpace(line)
        || (line.TrimStart().StartsWith('|')
            && !Regex.IsMatch(line, "`\\.ai/", RegexOptions.CultureInvariant, RegexTimeout));

    private static string TidyReferenceLine(string line)
    {
        while (Regex.IsMatch(line, @",\s*,", RegexOptions.CultureInvariant, RegexTimeout))
        {
            line = Regex.Replace(line, @",\s*,", ", ", RegexOptions.CultureInvariant, RegexTimeout);
        }

        line = Regex.Replace(line, @"\|\s*,", "|", RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @",\s*\|", " |", RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @";\s*invocable as\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @"\s+as\s*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @"\s+and\s*\.?\s*$", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @"\s+or\s*\.?\s*$", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @";\s*$", string.Empty, RegexOptions.CultureInvariant, RegexTimeout);
        line = Regex.Replace(line, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant, RegexTimeout);
        return line.TrimEnd();
    }

    private static string CopilotReference(string text)
    {
        if (text.Contains(CopilotReferenceStart, StringComparison.Ordinal)
            && text.Contains(CopilotReferenceEnd, StringComparison.Ordinal))
        {
            var start = text.IndexOf(CopilotReferenceStart, StringComparison.Ordinal) + CopilotReferenceStart.Length;
            var end = text.IndexOf(CopilotReferenceEnd, StringComparison.Ordinal);
            return text[start..end].Trim();
        }

        var parts = CopilotReferenceSections
            .Select(title => Section(text, title))
            .Where(part => part.Length > 0);
        return string.Join("\n\n", parts);
    }

    private static string? CopilotDocument(string agentsText, string? existing, InstalledNames names, string? runtime)
    {
        var reference = string.Empty;
        if (existing is not null)
        {
            existing = NormalizeMarkers(existing);
            var generated = existing.Contains(CopilotGenerated, StringComparison.Ordinal);
            var template = existing.Contains("## Work-Type Context Mapping", StringComparison.Ordinal)
                || existing.Contains("## Project Context", StringComparison.Ordinal);
            if (!generated && !template)
            {
                return null;
            }

            reference = FilterItemRefs(CopilotReference(existing), names).Trim();
        }

        var parts = new List<string>
        {
            "# GitHub Copilot Instructions",
            string.Empty,
            CopilotGenerated,
            CopilotNote,
            string.Empty,
            CopilotPaths(agentsText.Trim()),
        };
        if (reference.Length > 0)
        {
            parts.Add(string.Empty);
            parts.Add(CopilotReferenceStart);
            parts.Add(CopilotPaths(reference));
            parts.Add(CopilotReferenceEnd);
        }

        // The runtime section follows the reference block: a reader (and the template's own
        // validate-adapters.py) expects the generated body, then the preserved reference block,
        // then the runtime instructions it replaces.
        if (!string.IsNullOrWhiteSpace(runtime))
        {
            parts.Add(string.Empty);
            parts.Add(CopilotRuntimeStart);
            parts.Add(runtime.Trim());
            parts.Add(CopilotRuntimeEnd);
        }

        return string.Join("\n", parts).TrimEnd() + "\n";
    }

    /// <summary>
    /// The template-owned Copilot runtime section, copied fresh on every regeneration.
    /// A template that does not ship it yields <c>null</c> and the section is skipped, so a
    /// project pinned to an older template keeps working.
    /// </summary>
    private string? ReadCopilotRuntime(ManifestDocument manifest)
    {
        var entry = SourceCatalog.EntryNamed(manifest, ResolverDefaults.TemplateSource);
        var sourceRoot = queries.Sources.SourceRoot(ResolverDefaults.TemplateSource, entry);
        var path = Path.Combine(sourceRoot, CopilotRuntimeFile.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string NormalizeMarkers(string text) =>
        (text ?? string.Empty).Replace(LegacyMarkerPrefix, MarkerPrefix, StringComparison.Ordinal);

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;
    }

    private static string Section(string text, string title)
    {
        var match = SectionPattern(title).Match(text);
        return match.Success ? match.Value.Trim() : string.Empty;
    }

    private static string CopilotPaths(string text) => ClaudePathPattern().Replace(text, ".github/");

    [GeneratedRegex(
        "\\.ai/(?:skills/(?<skill>[A-Za-z0-9._-]+)/SKILL\\.md|agents/(?<agent>[A-Za-z0-9._-]+)\\.md)",
        RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex ItemRef();

    [GeneratedRegex(
        "## Installed skills and agents\\s*\\n.*?<!-- ai-workspace-manager:installed:end -->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex InstalledBlock();

    [GeneratedRegex("\\.claude/(?!settings\\.json|settings\\.local\\.json)", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex ClaudePathPattern();

    [GeneratedRegex("^## {TITLE}\\s*$.*?(?=^## |\\Z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex SectionTemplate();

    private static Regex SectionPattern(string title) =>
        new("^## " + Regex.Escape(title) + "\\s*$.*?(?=^## |\\Z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexTimeout);

    private sealed record InstalledCatalog(
        IReadOnlyList<(string Name, string Description)> Skills,
        IReadOnlyList<(string Name, string Description)> Agents);

    private sealed record InstalledNames(HashSet<string> Skills, HashSet<string> Agents);
}
