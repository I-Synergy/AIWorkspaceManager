using AiWorkspaceManager.Common;
using AiWorkspaceManager.Core.Catalog;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Manifest → declared selection → dependency closure. This is the rule engine behind browse,
/// status, sync, update and every mutation; ported from <c>_selection</c>, <c>_closure</c> and
/// <c>_preset_selection</c> in the Python resolver.
/// </summary>
public sealed class SelectionEngine
{
    private readonly IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey;
    private readonly PresetCatalog presets;

    public SelectionEngine(
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey,
        PresetCatalog presets)
    {
        this.indexByKey = indexByKey;
        this.presets = presets;
    }

    public static string CanonicalPresetName(string name)
    {
        var text = name.Trim();
        return text == "full" ? "Everything" : text;
    }

    /// <summary>Active presets in order: the legacy singleton first, then the additive list.</summary>
    public static IReadOnlyList<string> PresetNames(ManifestDocument manifest)
    {
        var raw = new List<string>();
        var legacy = manifest.GetString("preset");
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            raw.Add(legacy);
        }

        raw.AddRange(manifest.GetStringArray("presets").Where(name => !string.IsNullOrWhiteSpace(name)));

        var names = new List<string>();
        foreach (var name in raw)
        {
            var canonical = CanonicalPresetName(name);
            if (!names.Contains(canonical))
            {
                names.Add(canonical);
            }
        }

        return names;
    }

    public IReadOnlyList<string> ValidatePresetNames(IEnumerable<string> names)
    {
        var canonical = new List<string>();
        foreach (var raw in names)
        {
            var name = CanonicalPresetName(raw);
            if (!presets.Presets.ContainsKey(name))
            {
                var close = CloseMatches(name, presets.Presets.Keys, 3);
                var hint = close.Count > 0 ? $" (did you mean: {string.Join(", ", close)})" : string.Empty;
                throw new ResolverException($"unknown preset '{name}'{hint} — see catalog/presets.json");
            }

            if (!canonical.Contains(name))
            {
                canonical.Add(name);
            }
        }

        return canonical;
    }

    /// <summary>Expands every active preset into one {kind: [id]} selection.</summary>
    public Dictionary<string, List<string>> PresetSelection(IEnumerable<string> names, bool strict = true)
    {
        var expanded = ResolverDefaults.Kinds.ToDictionary(kind => kind, _ => new List<string>(), StringComparer.Ordinal);
        var validated = ValidatePresetNames(names);
        if (validated.Count == 0)
        {
            return expanded;
        }

        foreach (var name in validated)
        {
            var body = presets.Presets[name];
            foreach (var kind in ResolverDefaults.Kinds)
            {
                var ids = expanded[kind];
                var manifestKey = ResolverDefaults.ManifestKeys[kind];
                if (!body.Entries.TryGetValue(manifestKey, out var entries))
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    AddPresetEntry(ids, kind, name, entry, strict);
                }
            }
        }

        return expanded;
    }

    /// <summary>Adds one preset entry to a kind's id list, resolving <c>all</c> and bare names.</summary>
    private void AddPresetEntry(List<string> ids, string kind, string presetName, PresetEntry entry, bool strict)
    {
        if (entry.All)
        {
            var all = indexByKey.Keys
                .Where(key => key.Kind == kind)
                .OrderBy(key => key.Kind, StringComparer.Ordinal)
                .ThenBy(key => key.Id, StringComparer.Ordinal)
                .Select(key => key.Id)
                .Where(id => !ids.Contains(id));
            ids.AddRange(all);
            return;
        }

        var itemId = ItemId.Canonical(entry.Id ?? string.Empty);
        if (strict && !indexByKey.ContainsKey((kind, itemId)))
        {
            throw new ResolverException(
                $"preset '{presetName}' references unknown {kind} '{entry.Id}'");
        }

        if (!ids.Contains(itemId))
        {
            ids.Add(itemId);
        }
    }

    /// <summary>Preset ids only — what a card would add, with no manifest list and no excludes.</summary>
    public List<(string Kind, string Id)> PresetSelected(string name, bool strict = false)
    {
        var expanded = PresetSelection([name], strict);
        return ResolverDefaults.Kinds
            .SelectMany(kind => expanded[kind].Select(id => (Kind: kind, Id: id)))
            .ToList();
    }

    /// <summary>Expands the manifest into an ordered, de-duplicated [(kind, id)] list.</summary>
    public List<(string Kind, string Id)> Selection(ManifestDocument manifest, bool strict = true)
    {
        var excludes = Excludes(manifest);
        var presetIds = PresetSelection(PresetNames(manifest), strict);
        var selected = new List<(string Kind, string Id)>();

        foreach (var kind in ResolverDefaults.Kinds)
        {
            var manifestKey = ResolverDefaults.ManifestKeys[kind];
            var rawIds = new List<string>(presetIds[kind]);
            rawIds.AddRange(manifest.GetStringArray(manifestKey));

            foreach (var raw in rawIds)
            {
                var itemId = ResolveManifestId(kind, raw);
                if (strict)
                {
                    EnsureKnown(kind, manifestKey, raw, itemId);
                }

                if (excludes.Contains(itemId) || selected.Contains((kind, itemId)))
                {
                    continue;
                }

                selected.Add((kind, itemId));
            }
        }

        return selected;
    }

    /// <summary>Throws a helpful error when a strict selection names something the catalog lacks.</summary>
    private void EnsureKnown(string kind, string manifestKey, string raw, string itemId)
    {
        if (indexByKey.ContainsKey((kind, itemId)))
        {
            return;
        }

        var other = kind == ResolverDefaults.SkillKind ? ResolverDefaults.AgentKind : ResolverDefaults.SkillKind;
        if (indexByKey.ContainsKey((other, itemId)))
        {
            throw new ResolverException(
                $"'{ItemId.Display(itemId)}' is an {other}, but it is listed under "
                + $"'{manifestKey}' in ai.json");
        }

        throw new ResolverException(
            $"unknown id '{raw}' — not in the catalog "
            + "(run 'catalog.py index' if the tree changed)");
    }

    /// <summary>The selection plus everything it requires, dependencies first.</summary>
    public List<(string Kind, string Id)> Closure(IEnumerable<(string Kind, string Id)> selection)
    {
        var ordered = new List<(string Kind, string Id)>();
        var done = new HashSet<(string Kind, string Id)>();
        var visiting = new List<(string Kind, string Id)>();

        void Visit(string kind, string itemId, string? requiredBy)
        {
            var key = (Kind: kind, Id: itemId);
            if (done.Contains(key))
            {
                return;
            }

            if (visiting.Contains(key))
            {
                var chain = string.Join(
                    " -> ",
                    visiting.Append(key).Select(entry => ItemId.Display(entry.Id)));
                throw new ResolverException($"dependency cycle in the catalog: {chain}");
            }

            if (!indexByKey.TryGetValue(key, out var item))
            {
                // A dependency the catalog cannot resolve is a broken catalog, not a user typo:
                // fail here rather than installing a selection that is quietly incomplete.
                if (requiredBy is not null)
                {
                    throw new ResolverException(
                        $"the catalog declares a dependency that does not exist: "
                        + $"{ItemId.Display(itemId)} (required by {ItemId.Display(requiredBy)})");
                }

                return;
            }

            visiting.Add(key);
            foreach (var dependency in item.Requires)
            {
                if (DependencyKey(dependency) is not { } resolved)
                {
                    throw new ResolverException(
                        $"the catalog declares a dependency that does not exist: "
                        + $"{dependency} (required by {ItemId.Display(itemId)})");
                }

                Visit(resolved.Kind, resolved.ItemId, itemId);
            }

            visiting.RemoveAt(visiting.Count - 1);
            done.Add(key);
            ordered.Add(key);
        }

        foreach (var (kind, id) in selection)
        {
            Visit(kind, id, null);
        }

        return ordered;
    }

    public List<(string Kind, string Id)> EffectiveSelection(ManifestDocument manifest, bool strict = true) =>
        Closure(Selection(manifest, strict));

    /// <summary>Normalises a manifest entry, healing a legacy bare third-party id.</summary>
    public string ResolveManifestId(string kind, string raw)
    {
        var text = raw.Trim();
        var itemId = ItemId.Canonical(text);
        if (text.Contains(':', StringComparison.Ordinal) || indexByKey.ContainsKey((kind, itemId)))
        {
            return itemId;
        }

        var matches = BareMatches(text, kind);
        return matches.Count == 1 ? matches[0] : itemId;
    }

    /// <summary>
    /// The catalog key a <c>requires:</c> entry names. Null when the catalog has nothing that
    /// matches. See <see cref="ResolveDependency"/> for the forms an entry may take.
    /// </summary>
    public (string Kind, string ItemId)? DependencyKey(string raw) => ResolveDependency(raw);

    /// <summary>
    /// Resolves one <c>requires:</c> entry. Four forms are accepted, in this order:
    /// <list type="bullet">
    /// <item><c>agent:name</c> — an agent, whatever else carries the name.</item>
    /// <item><c>skill:name</c> — a skill, whatever else carries the name.</item>
    /// <item>a full catalog id such as <c>template:alpha</c> — the item it names.</item>
    /// <item>a bare name — the skill, then the agent. A skill wins a name both kinds carry, so
    /// catalogs written before agents could be dependencies keep resolving.</item>
    /// </list>
    /// A name that matches more than one item of the chosen kind is ambiguous, not a dependency.
    /// </summary>
    private (string Kind, string ItemId)? ResolveDependency(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var (prefix, kind) in KindPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var matches = BareMatches(text[prefix.Length..].Trim(), kind);
            return matches.Count == 1 ? (kind, matches[0]) : null;
        }

        var exact = indexByKey.Keys
            .Where(key => string.Equals(key.Id, text, StringComparison.Ordinal))
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        // A full id both kinds carry (`template:security`) names the skill, matching the bare-name
        // rule, so a catalog that stores ids keeps resolving the way it did before agents existed.
        if (exact.Count > 1)
        {
            var skill = exact.Where(key => key.Kind == ResolverDefaults.SkillKind).ToList();
            return skill.Count == 1 ? skill[0] : null;
        }

        foreach (var kind in new[] { ResolverDefaults.SkillKind, ResolverDefaults.AgentKind })
        {
            var matches = BareMatches(text, kind);
            if (matches.Count == 1)
            {
                return (kind, matches[0]);
            }
        }

        return null;
    }

    /// <summary>Kind-qualified prefixes a <c>requires:</c> entry may use.</summary>
    private static readonly (string Prefix, string Kind)[] KindPrefixes =
    [
        (ResolverDefaults.AgentKind + ":", ResolverDefaults.AgentKind),
        (ResolverDefaults.SkillKind + ":", ResolverDefaults.SkillKind),
    ];

    /// <summary>
    /// Every dependency <paramref name="roots"/> pull in, transitively, as the keys the catalog
    /// resolves them to. Dependencies outside the catalog are left out: sync reports them.
    /// </summary>
    public IReadOnlyList<(string Kind, string Id)> RequiredClosure(IEnumerable<(string Kind, string Id)> roots)
    {
        var found = new List<(string Kind, string Id)>();
        var done = new HashSet<(string Kind, string Id)>();
        var pending = new Queue<(string Kind, string Id)>(roots);
        while (pending.Count > 0)
        {
            var key = pending.Dequeue();
            if (!done.Add(key) || !indexByKey.TryGetValue(key, out var item))
            {
                continue;
            }

            foreach (var dependency in item.Requires)
            {
                if (DependencyKey(dependency) is not { } resolved || !done.Contains(resolved))
                {
                    continue;
                }

                found.Add(resolved);
                pending.Enqueue(resolved);
            }
        }

        return found;
    }

    /// <summary>
    /// The selection plus the dependencies it installs, each tagged with the item that pulled it
    /// in. Already-selected items are tagged <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <c>seen</c> is seeded from the selection, so a dependency that is already selected is not
    /// tagged. The tag is the id of the entry being expanded, read from the entry itself so it
    /// cannot be confused with the tuple's inferred element names.
    /// </remarks>
    public List<(string Kind, string Id, string? RequiredBy)> WithDependencies(
        IEnumerable<(string Kind, string Id)> selection)
    {
        var entries = new List<(string Kind, string Id, string? RequiredBy)>();
        var seen = new HashSet<(string Kind, string Id)>();
        foreach (var (kind, itemId) in selection)
        {
            if (seen.Add((kind, itemId)))
            {
                entries.Add((kind, itemId, null));
            }
        }

        for (var position = 0; position < entries.Count; position++)
        {
            var (kind, itemId, _) = entries[position];
            if (!indexByKey.TryGetValue((kind, itemId), out var item))
            {
                continue;
            }

            foreach (var dependency in item.Requires)
            {
                if (DependencyKey(dependency) is not { } resolved || !seen.Add(resolved))
                {
                    continue;
                }

                entries.Add((resolved.Kind, resolved.ItemId, itemId));
            }
        }

        return entries;
    }

    /// <summary>Catalog ids whose manifest name is <paramref name="name"/>, optionally of one kind.</summary>
    public IReadOnlyList<string> BareMatches(string name, string? kind = null) =>
        indexByKey.Keys
            .Where(key => (kind is null || key.Kind == kind) && ItemId.Split(key.Id).Name == name)
            .Select(key => key.Id)
            .ToList();

    /// <summary>Selection ids the user froze: update and sync never overwrite them.</summary>
    public HashSet<string> LockedIds(ManifestDocument manifest)
    {
        var frozen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in manifest.GetStringArray("locked"))
        {
            var text = raw.Trim();
            var itemId = ItemId.SoftCanonical(text);
            if (itemId is null)
            {
                continue;
            }

            if (!text.Contains(':', StringComparison.Ordinal)
                && !indexByKey.Keys.Any(key => key.Id == itemId))
            {
                var matches = BareMatches(text);
                if (matches.Count == 1)
                {
                    itemId = matches[0];
                }
            }

            frozen.Add(itemId);
        }

        return frozen;
    }

    public HashSet<string> Excludes(ManifestDocument manifest)
    {
        var excludes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in manifest.GetStringArray("exclude"))
        {
            var text = raw.Trim();
            var normalized = ItemId.SoftCanonical(text);
            if (normalized is null)
            {
                continue;
            }

            if (!text.Contains(':', StringComparison.Ordinal)
                && !indexByKey.Keys.Any(key => key.Id == normalized))
            {
                var matches = BareMatches(text);
                if (matches.Count == 1)
                {
                    normalized = matches[0];
                }
            }

            excludes.Add(normalized);
        }

        return excludes;
    }

    /// <summary>The selected AI clients; a missing field means every legacy client.</summary>
    public static HashSet<string> Clients(ManifestDocument manifest)
    {
        if (!manifest.Has("clients") || manifest.Root["clients"] is null)
        {
            return ResolverDefaults.Clients.ToHashSet(StringComparer.Ordinal);
        }

        if (manifest.Root["clients"] is not System.Text.Json.Nodes.JsonArray array)
        {
            throw new ResolverException(
                $"clients must be a list of {string.Join(", ", ResolverDefaults.Clients.Select(name => $"'{name}'"))}");
        }

        var names = array
            .Where(node => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out _))
            .Select(node => node!.GetValue<string>().Trim().ToLowerInvariant())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var known = ResolverDefaults.Clients.ToHashSet(StringComparer.Ordinal);
        var unknown = names.Except(known).OrderBy(name => name, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ResolverException(
                $"unknown client(s): {string.Join(", ", unknown)} — "
                + $"choose from {string.Join(", ", ResolverDefaults.Clients)}");
        }

        return names;
    }

    /// <summary>Hooks run scripts, so installing one needs per-source opt-in.</summary>
    public void CheckHookGate(
        ManifestDocument manifest,
        IEnumerable<(string Kind, string Id)> selected,
        SourceCatalog sources,
        WorkspacePaths paths)
    {
        var blocked = new List<(string Id, string Source)>();
        foreach (var (kind, itemId) in selected)
        {
            if (kind != "hook")
            {
                continue;
            }

            var source = indexByKey.TryGetValue((kind, itemId), out var item)
                ? item.Source
                : ResolverDefaults.TemplateSource;
            var entry = SourceCatalog.EntryNamed(manifest, source);
            var allowed = entry["allowHooks"] is System.Text.Json.Nodes.JsonValue value
                && value.TryGetValue<bool>(out var allow)
                && allow;
            if (allowed)
            {
                continue;
            }

            if (!WorkspacePaths.SamePath(sources.SourceRoot(source, entry), paths.Root))
            {
                blocked.Add((itemId, source));
            }
        }

        if (blocked.Count == 0)
        {
            return;
        }

        var detail = string.Join(
            ", ",
            blocked.Take(5).Select(entry => $"{ItemId.Display(entry.Id)} (source '{entry.Source}')"));
        var more = blocked.Count <= 5 ? string.Empty : $" and {blocked.Count - 5} more";
        throw new ResolverException(
            $"refusing to install hooks: {detail}{more} — add \"allowHooks\": true to that "
            + "source in ai.json to accept them");
    }

    private static List<string> CloseMatches(string value, IEnumerable<string> candidates, int limit)
    {
        return candidates
            .Select(candidate => (Candidate: candidate, Score: Similarity(value, candidate)))
            .Where(pair => pair.Score >= 0.6)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Candidate, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => pair.Candidate)
            .ToList();
    }

    /// <summary>A normalised edit distance in [0, 1], used only for suggestions.</summary>
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 1;
        }

        var distance = Levenshtein(left, right);
        return 1.0 - ((double)distance / Math.Max(left.Length, right.Length));
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
