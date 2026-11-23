using System.Text.Json;

using AiWorkspaceManager.Common;

namespace AiWorkspaceManager.Core.Catalog;

/// <summary>
/// Walks every resolved source and builds the sorted catalog index. This is the read-only half of
/// the resolver: no manifest, no lock, no writes.
/// </summary>
public sealed class CatalogIndexBuilder
{
    private const string InstructionsSuffix = ".instructions.md";
    private const string PromptSuffix = ".prompt.md";
    private const string AgentSuffix = ".agent.md";
    private const string MarkdownSuffix = ".md";
    private const string ReadmeName = "README.md";

    private static readonly string[] PluginMemberSuffixes = [AgentSuffix, InstructionsSuffix, MarkdownSuffix];
    private static readonly string[] PluginMemberKeys = ["agents", "skills"];

    private readonly IReadOnlyList<CatalogSource> sources;
    private readonly CategoryVocabulary vocabulary;
    private readonly Action<string>? note;

    public CatalogIndexBuilder(
        IEnumerable<CatalogSource> sources,
        CategoryVocabulary? vocabulary = null,
        Action<string>? note = null)
    {
        this.sources = sources.ToList();
        this.vocabulary = vocabulary ?? CategoryVocabulary.Empty;
        this.note = note;
    }

    public CatalogIndex Build()
    {
        var entries = new List<BuildEntry>();
        var seen = new HashSet<(string Kind, string Id)>();
        var materialised = new Dictionary<(string Kind, string Name), string>();
        var shadowed = new List<ShadowedItem>();

        foreach (var source in sources)
        {
            foreach (var kind in ResolverDefaults.Kinds)
            {
                foreach (var entry in EnumerateEntries(kind, source))
                {
                    if (ReadEntry(kind, source, entry, seen, materialised, shadowed) is { } built)
                    {
                        entries.Add(built);
                    }
                }
            }
        }

        ReportShadowed(shadowed);

        entries.Sort((left, right) =>
        {
            var compared = string.CompareOrdinal(left.Item.Id, right.Item.Id);
            return compared != 0 ? compared : string.CompareOrdinal(left.Item.Kind, right.Item.Kind);
        });

        var idsByName = entries.ToDictionary(
            entry => (entry.Item.Kind, entry.Item.Name),
            entry => entry.Item.Id);
        var items = entries
            .Select(entry => CopyWithRequires(entry.Item, entry.Declared, idsByName))
            .ToList();

        return new CatalogIndex { Shadowed = shadowed, Items = items };
    }

    /// <summary>
    /// Reads one candidate path into a catalog entry, or null when it is not an item. Records
    /// duplicates as errors and same-named items from later sources as shadowed.
    /// </summary>
    private BuildEntry? ReadEntry(
        string kind,
        CatalogSource source,
        string entry,
        HashSet<(string Kind, string Id)> seen,
        Dictionary<(string Kind, string Name), string> materialised,
        List<ShadowedItem> shadowed)
    {
        var document = KindDocument(kind, entry);
        if (document is null)
        {
            return null;
        }

        var name = ItemName(kind, entry);
        if (name is null)
        {
            return null;
        }

        var sourceRelative = RelativeToSource(source.Root, entry);
        var installedPath =
            $".ai/{ResolverDefaults.KindDirectories[kind]}/{name}{ResolverDefaults.KindSuffixes[kind]}";

        var raw = File.ReadAllText(document);
        var (description, declared) = kind is ResolverDefaults.PluginKind or ResolverDefaults.HookKind
            ? ManifestMetadata(kind, raw)
            : ItemMetadata(kind, raw);

        var itemId = ItemId.Make(source.Name, name);
        if (!seen.Add((kind, itemId)))
        {
            throw new ResolverException(
                $"duplicate catalog id '{itemId}' for kind {kind} at {installedPath}");
        }

        if (materialised.TryGetValue((kind, name), out var previous)
            && !string.Equals(previous, itemId, StringComparison.Ordinal))
        {
            shadowed.Add(new ShadowedItem(itemId, previous, installedPath));
            return null;
        }

        materialised[(kind, name)] = itemId;
        var (hash, count) = ContentHash.ItemHash(entry);
        var (category, tags) = Classify(source.Name, raw, kind);
        return new BuildEntry(
            new CatalogItem
            {
                Id = itemId,
                Source = source.Name,
                Kind = kind,
                Name = name,
                Path = installedPath,
                SourcePath = sourceRelative,
                Description = description,
                Category = category,
                Tags = tags,
                Ref = source.Ref,
                Sha256 = hash,
                Files = count,
                License = source.License,
                Upstream = $"https://github.com/{source.Repo}/tree/{source.Ref}/{sourceRelative}",
            },
            declared);
    }

    private static IEnumerable<string> EnumerateEntries(string kind, CatalogSource source)
    {
        var directories = SourceKindDirectories(kind, source);
        var entries = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            entries.AddRange(Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal));
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.') || string.Equals(name, "__pycache__", StringComparison.Ordinal))
            {
                continue;
            }

            yield return entry;
        }
    }

    private static List<string> SourceKindDirectories(string kind, CatalogSource source)
    {
        if (source.Paths is { } configured)
        {
            var directory = ResolverDefaults.KindDirectories[kind];
            string? key = null;
            if (configured.ContainsKey(kind))
            {
                key = kind;
            }
            else if (configured.ContainsKey(directory))
            {
                key = directory;
            }

            if (key is not null)
            {
                return (configured[key] ?? [])
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => System.IO.Path.Combine(source.Root, part))
                    .ToList();
            }
        }

        var dotted = System.IO.Path.Combine(source.Root, ".ai", ResolverDefaults.KindDirectories[kind]);
        if (source.Name == ResolverDefaults.TemplateSource || Directory.Exists(dotted))
        {
            return [dotted];
        }

        return [System.IO.Path.Combine(source.Root, ResolverDefaults.KindDirectories[kind])];
    }

    private static string? KindDocument(string kind, string entry)
    {
        switch (kind)
        {
            case ResolverDefaults.SkillKind:
                return DirectoryDocument(entry, "SKILL.md");

            case ResolverDefaults.PluginKind:
                return DirectoryDocument(entry, "plugin.json");

            case ResolverDefaults.HookKind:
                return DirectoryDocument(entry, "hooks.json");

            default:
                return MarkdownDocument(kind, entry);
        }
    }

    private static string? DirectoryDocument(string entry, string fileName)
    {
        var document = Path.Combine(entry, fileName);
        return Directory.Exists(entry) && File.Exists(document) ? document : null;
    }

    /// <summary>Agent, instruction and prompt files share one folder, so each kind excludes the others' suffixes.</summary>
    private static string? MarkdownDocument(string kind, string entry)
    {
        if (!File.Exists(entry) || !entry.EndsWith(MarkdownSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = Path.GetFileName(entry);
        var readme = string.Equals(name, ReadmeName, StringComparison.OrdinalIgnoreCase);
        var foreign = kind switch
        {
            ResolverDefaults.AgentKind => new[] { InstructionsSuffix, PromptSuffix },
            ResolverDefaults.InstructionKind => [AgentSuffix, PromptSuffix],
            ResolverDefaults.PromptKind => [InstructionsSuffix, AgentSuffix],
            _ => null,
        };

        if (foreign is null)
        {
            return null;
        }

        var belongsToOtherKind = foreign.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
        return belongsToOtherKind || readme ? null : entry;
    }

    private static string? ItemName(string kind, string entry)
    {
        var fileName = Path.GetFileName(entry);
        switch (kind)
        {
            case ResolverDefaults.SkillKind:
            case ResolverDefaults.PluginKind:
            case ResolverDefaults.HookKind:
                return fileName;

            case ResolverDefaults.AgentKind:
                {
                    var name = fileName[..^MarkdownSuffix.Length];
                    return name.EndsWith(".agent", StringComparison.Ordinal)
                        ? name[..^".agent".Length]
                        : name;
                }

            case ResolverDefaults.InstructionKind:
                return fileName.EndsWith(InstructionsSuffix, StringComparison.Ordinal)
                    ? fileName[..^InstructionsSuffix.Length]
                    : fileName[..^MarkdownSuffix.Length];

            case ResolverDefaults.PromptKind:
                return fileName.EndsWith(PromptSuffix, StringComparison.Ordinal)
                    ? fileName[..^PromptSuffix.Length]
                    : fileName[..^MarkdownSuffix.Length];

            default:
                return null;
        }
    }

    private static (string Description, IReadOnlyList<string> Declared) ItemMetadata(string kind, string raw)
    {
        var meta = Frontmatter.Parse(raw);
        var description = Frontmatter.OneLine(meta.GetString("description"));
        // A skill names its dependencies in `requires:`. An agent names the skills it drives in
        // `skills:` and, since agents can hand work to other agents, may also use `requires:`.
        IReadOnlyList<string> declared = kind == ResolverDefaults.AgentKind
            ? [.. meta.GetNameList("skills"), .. meta.GetNameList("requires")]
            : meta.GetNameList("requires");
        return (description, declared);
    }

    private static (string Description, IReadOnlyList<string> Declared) ManifestMetadata(string kind, string raw)
    {
        string name = string.Empty;
        string description = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("name", out var nameValue)
                    && nameValue.ValueKind == JsonValueKind.String)
                {
                    name = nameValue.GetString() ?? string.Empty;
                }

                if (document.RootElement.TryGetProperty("description", out var descriptionValue)
                    && descriptionValue.ValueKind == JsonValueKind.String)
                {
                    description = descriptionValue.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // A broken manifest is reported by the validator, not by the index.
        }

        _ = name;
        return (Frontmatter.OneLine(description),
            kind == ResolverDefaults.PluginKind ? PluginMembers(raw) : []);
    }

    private static string StripExtension(string name)
    {
        var suffix = PluginMemberSuffixes.FirstOrDefault(s => name.EndsWith(s, StringComparison.Ordinal));
        return suffix is null ? name : name[..^suffix.Length];
    }

    private static List<string> PluginMembers(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("extensions", out var extensions)
                || extensions.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            return extensions.EnumerateObject()
                .Where(block => block.Value.ValueKind == JsonValueKind.Object)
                .SelectMany(block => PluginMemberNames(block.Value))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The item names a plugin's <c>agents</c> and <c>skills</c> path lists point at.</summary>
    private static IEnumerable<string> PluginMemberNames(JsonElement block)
    {
        foreach (var key in PluginMemberKeys)
        {
            if (!block.TryGetProperty(key, out var paths) || paths.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var path in paths.EnumerateArray().Where(path => path.ValueKind == JsonValueKind.String))
            {
                var cleaned = StripExtension((path.GetString() ?? string.Empty)
                    .Trim()
                    .TrimEnd('/')
                    .Split('/')[^1]);
                if (cleaned.Length > 0)
                {
                    yield return cleaned;
                }
            }
        }
    }

    private (string Category, IReadOnlyList<string> Tags) Classify(
        string sourceName,
        string raw,
        string kind)
    {
        var meta = kind is ResolverDefaults.PluginKind or ResolverDefaults.HookKind
            ? Frontmatter.Empty
            : Frontmatter.Parse(raw);
        var tags = meta.GetNameList("tags");
        var defined = vocabulary.Ids().ToHashSet(StringComparer.Ordinal);

        if (vocabulary.Sources.TryGetValue(sourceName, out var rules)
            && !string.IsNullOrWhiteSpace(rules.Category))
        {
            return (rules.Category.Trim(), tags);
        }

        var declared = meta.GetString("category")?.Trim();
        if (!string.IsNullOrEmpty(declared))
        {
            return (declared, tags.Where(tag => tag != declared).ToList());
        }

        var match = tags.FirstOrDefault(defined.Contains);
        if (match is not null)
        {
            return (match, tags.Where(entry => entry != match).ToList());
        }

        var fallback = rules?.Default;
        return (string.IsNullOrWhiteSpace(fallback) ? "uncategorised" : fallback, tags);
    }

    private static string RelativeToSource(string root, string entry)
    {
        var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(entry) : relative;
    }

    /// <summary>
    /// Turns declared names into catalog ids. A dependency may name a skill or an agent, so the
    /// lookup tries both; an unresolved name falls back to a skill id in the same source.
    /// </summary>
    /// <summary>
    /// The catalog id a declared dependency names. An agent's <c>skills:</c> entry names a skill,
    /// so it never resolves to an agent. A <c>requires:</c> entry may name either kind: a
    /// kind-qualified entry picks that kind outright, otherwise a skill wins over an agent so
    /// existing catalogs keep resolving. An unknown name becomes a skill id in this source and
    /// surfaces later as an unresolved dependency.
    /// </summary>
    private static string ResolveDependencyId(
        CatalogItem item,
        string name,
        Dictionary<(string Kind, string Name), string> idsByName)
    {
        if (name.StartsWith(ResolverDefaults.AgentKind + ":", StringComparison.Ordinal)
            && idsByName.TryGetValue((ResolverDefaults.AgentKind, name), out var qualifiedAgent))
        {
            return qualifiedAgent;
        }

        if (idsByName.TryGetValue((ResolverDefaults.SkillKind, name), out var skillId))
        {
            return skillId;
        }

        if (idsByName.TryGetValue((ResolverDefaults.AgentKind, name), out var agentId))
        {
            return agentId;
        }

        return ItemId.Make(item.Source, name);
    }

    /// <summary>
    /// Turns declared names into catalog ids. An agent's <c>skills:</c> names skills only, so it
    /// resolves against skills; a <c>requires:</c> entry may name either kind. An item never
    /// depends on itself: `security` is both a skill and an agent, and the agent listing the
    /// `security` skill must not resolve to its own id.
    /// </summary>
    private static CatalogItem CopyWithRequires(
        CatalogItem item,
        IReadOnlyList<string> declared,
        Dictionary<(string Kind, string Name), string> idsByName)
    {
        var requires = declared
            .Select(name => ResolveDependencyId(item, name, idsByName))
            .Where(id => !string.Equals(id, item.Id, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new CatalogItem
        {
            Id = item.Id,
            Source = item.Source,
            Kind = item.Kind,
            Name = item.Name,
            Path = item.Path,
            SourcePath = item.SourcePath,
            Description = item.Description,
            Category = item.Category,
            Tags = item.Tags,
            Ref = item.Ref,
            Sha256 = item.Sha256,
            Files = item.Files,
            License = item.License,
            Upstream = item.Upstream,
            Requires = requires,
        };
    }

    private void ReportShadowed(List<ShadowedItem> shadowed)
    {
        if (shadowed.Count == 0 || note is null)
        {
            return;
        }

        var shown = string.Join(", ", shadowed.Take(5).Select(entry => entry.Id));
        var more = shadowed.Count <= 5 ? string.Empty : $" and {shadowed.Count - 5} more";
        note($"note: {shadowed.Count} item(s) shadowed by a same-named item from an earlier source: {shown}{more}");
    }

    private sealed record BuildEntry(CatalogItem Item, IReadOnlyList<string> Declared);
}
