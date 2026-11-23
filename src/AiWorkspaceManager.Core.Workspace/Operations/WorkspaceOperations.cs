using System.Text.Json.Nodes;
using AiWorkspaceManager.Common;
using AiWorkspaceManager.Core.Catalog;

namespace AiWorkspaceManager.Core.Workspace;

/// <summary>
/// Mutating commands: add/remove, locks, sync, update, pin and preset edits. The Python resolver
/// called these <c>cmd_add</c>, <c>cmd_remove</c>, <c>cmd_sync</c>, <c>cmd_update</c> and
/// <c>cmd_pin</c>; the rules (lock safety, prune boundary, orphan refusal) are preserved.
/// </summary>
public sealed class WorkspaceOperations
{
    private readonly WorkspaceQueries queries;
    private readonly WorkspaceSetupService setup;

    public WorkspaceOperations(WorkspaceQueries queries, WorkspaceSetupService? setup = null)
    {
        this.queries = queries;
        this.setup = setup ?? new WorkspaceSetupService(queries);
    }

    public WorkspaceQueries Queries => queries;

    public OperationOutcome Add(IReadOnlyList<string> ids, string? kindFlag = null)
    {
        var existing = queries.LoadManifest();
        var manifest = existing ?? queries.StarterManifest().Bind(queries.Paths.ManifestPath);
        var index = queries.BuildIndex(manifest);
        var indexByKey = index.ByKey();

        var (resolved, problems) = ResolveAddTargets(ids, kindFlag, indexByKey);
        if (problems.Count > 0)
        {
            return new OperationOutcome(
                new CliResult(2, string.Empty, string.Join("\n", problems.Select(problem => $"ERROR: {problem}"))),
                null);
        }

        var added = new List<ItemRef>();
        var dependencies = new List<RequiredItem>();
        var engine = queries.Engine(index);
        foreach (var (kind, itemId, requiredBy) in engine.WithDependencies(resolved))
        {
            if (!SelectItem(manifest, kind, itemId, requiredBy, added, dependencies))
            {
                continue;
            }

            RemoveFromExcludes(manifest, itemId);
        }

        if (added.Count > 0 || dependencies.Count > 0 || existing is null)
        {
            manifest.Save();
        }

        var payload = new AddPayload(added, queries.Paths.ManifestPath, dependencies);
        var lines = new List<string>();
        if (added.Count > 0)
        {
            lines.Add($"added {added.Count} item(s) to {queries.Paths.ManifestPath}:");
            lines.AddRange(added.Select(entry => $"  + {entry.Kind,-6} {ItemId.Display(entry.Id)}"));
        }

        if (dependencies.Count > 0)
        {
            lines.Add($"added {dependencies.Count} dependency(ies) required by the selection:");
            lines.AddRange(dependencies.Select(entry =>
                $"  + {entry.Kind,-6} {ItemId.Display(entry.Id)} (required by {ItemId.Display(entry.RequiredBy)})"));
        }

        if (added.Count > 0 || dependencies.Count > 0)
        {
            lines.Add("run 'catalog.py sync' to materialise them");
        }
        else
        {
            lines.Add("nothing to add — already selected");
        }

        return new OperationOutcome(new CliResult(0, JoinLines(lines), string.Empty), payload);
    }

    /// <summary>Adds one item to its manifest list unless it is already there.</summary>
    private static bool SelectItem(
        ManifestDocument manifest,
        string kind,
        string itemId,
        string? requiredBy,
        List<ItemRef> added,
        List<RequiredItem> dependencies)
    {
        var selection = manifest.EnsureArray(ResolverDefaults.ManifestKeys[kind]);
        var strings = selection
            .Select(ToStringOrNull)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
        var bare = ItemId.Display(itemId);
        if (strings.Contains(bare) || strings.Contains(itemId))
        {
            return false;
        }

        selection.Add(bare);
        if (requiredBy is null)
        {
            added.Add(new ItemRef(itemId, kind));
        }
        else
        {
            dependencies.Add(new RequiredItem(itemId, kind, ItemId.Display(requiredBy)));
        }

        return true;
    }

    /// <summary>Drops a stale <c>exclude</c> entry for an item that is now selected.</summary>
    private static void RemoveFromExcludes(ManifestDocument manifest, string itemId)
    {
        var excludes = manifest.EnsureArray(ResolverDefaults.ExcludeKey);
        for (var position = excludes.Count - 1; position >= 0; position--)
        {
            if (ItemId.SoftCanonical(ToStringOrNull(excludes[position])) == itemId)
            {
                excludes.RemoveAt(position);
            }
        }
    }

    public OperationOutcome Remove(
        IReadOnlyList<string> ids,
        string? kindFlag = null,
        bool prune = false,
        bool force = false)
    {
        var manifest = queries.LoadManifest()
            ?? throw new ResolverException(
                $"no manifest at {queries.Paths.ManifestPath} — nothing to remove");

        var removed = RemoveFromManifestLists(manifest, ids, kindFlag);

        var index = queries.BuildIndex(manifest);
        var indexByKey = index.ByKey();
        var engine = queries.Engine(index);

        AddExcludesForPresetItems(manifest, ids, kindFlag, engine, removed);

        if (removed.Count == 0)
        {
            EnsureNotDependencyOnly(manifest, ids, engine, force);
        }

        if (removed.Count > 0 && !force)
        {
            EnsureNothingIsOrphaned(manifest, removed, engine, indexByKey);
        }

        if (removed.Count > 0)
        {
            RemoveFromLockedList(manifest, removed);
            manifest.Save();
        }

        var pruned = prune ? PruneRemoved(removed) : [];

        var payload = new RemovePayload(removed, pruned);
        if (removed.Count == 0)
        {
            return new OperationOutcome(
                new CliResult(0, "nothing to remove — no such selection in the manifest", string.Empty),
                payload);
        }

        var output = new List<string>();
        output.AddRange(removed.Select(entry => $"  - {entry.Kind,-6} {ItemId.Display(entry.Id)}"));
        if (prune)
        {
            if (pruned.Count == 0)
            {
                output.Add("  nothing lock-recorded to prune");
            }
            else
            {
                output.AddRange(pruned.Select(entry =>
                    $"  pruned {entry.Path} ({(entry.Deleted ? "deleted" : "not on disk")})"));
            }
        }
        else
        {
            output.Add("installed files were left alone: add --prune to delete lock-recorded paths");
        }

        return new OperationOutcome(new CliResult(0, JoinLines(output), string.Empty), payload);
    }

    /// <summary>Drops the ids from every manifest list they appear in.</summary>
    private static List<RemovedItem> RemoveFromManifestLists(
        ManifestDocument manifest,
        IReadOnlyList<string> ids,
        string? kindFlag)
    {
        var removed = new List<RemovedItem>();
        foreach (var raw in ids)
        {
            var itemId = ItemId.Canonical(raw);
            foreach (var kind in ResolverDefaults.Kinds)
            {
                if (!string.IsNullOrEmpty(kindFlag) && kind != kindFlag)
                {
                    continue;
                }

                var key = ResolverDefaults.ManifestKeys[kind];
                var selection = manifest.GetStringArray(key).ToList();
                var kept = selection.Where(value => ItemId.SoftCanonical(value) != itemId).ToList();
                if (kept.Count != selection.Count)
                {
                    SetStringArray(manifest, key, kept);
                    removed.Add(new RemovedItem(itemId, kind, "list"));
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Items a preset brings in are removed by listing them under <c>exclude</c>; a preset
    /// cannot be edited per item.
    /// </summary>
    private static void AddExcludesForPresetItems(
        ManifestDocument manifest,
        IReadOnlyList<string> ids,
        string? kindFlag,
        SelectionEngine engine,
        List<RemovedItem> removed)
    {
        var declared = new HashSet<(string Kind, string Id)>();
        foreach (var (kind, presetIds) in engine.PresetSelection(SelectionEngine.PresetNames(manifest), strict: false))
        {
            foreach (var itemId in presetIds)
            {
                declared.Add((kind, itemId));
            }
        }

        var selected = engine.Selection(manifest, strict: false);
        foreach (var raw in ids)
        {
            var itemId = ItemId.Canonical(raw);
            var match = selected.FirstOrDefault(entry =>
                entry.Id == itemId && declared.Contains((entry.Kind, entry.Id)));
            if (match == default || (!string.IsNullOrEmpty(kindFlag) && match.Kind != kindFlag))
            {
                continue;
            }

            var excludes = manifest.EnsureArray(ResolverDefaults.ExcludeKey);
            var present = excludes
                .Select(ToStringOrNull)
                .Any(value => ItemId.SoftCanonical(value) == itemId);
            if (present || removed.Any(entry => entry.Id == itemId))
            {
                continue;
            }

            excludes.Add(ItemId.Display(itemId));
            removed.Add(new RemovedItem(itemId, match.Kind, ResolverDefaults.ExcludeKey));
        }
    }

    /// <summary>Refuses to "remove" something that is only present because another item requires it.</summary>
    private static void EnsureNotDependencyOnly(
        ManifestDocument manifest,
        IReadOnlyList<string> ids,
        SelectionEngine engine,
        bool force)
    {
        var installed = engine.Closure(engine.Selection(manifest, strict: false))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var pulled = ids.Select(ItemId.Canonical).Where(installed.Contains).ToList();
        if (pulled.Count == 0 || force)
        {
            return;
        }

        var names = string.Join(", ", pulled.Select(id => $"'{ItemId.Display(id)}'"));
        throw new ResolverException(
            $"{names} is not declared in ai.json — it is installed because something else "
            + "requires it. Remove the dependent instead.");
    }

    /// <summary>Throws when a remaining item still requires one of the items being removed.</summary>
    private static void EnsureNothingIsOrphaned(
        ManifestDocument manifest,
        List<RemovedItem> removed,
        SelectionEngine engine,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey)
    {
        var remaining = engine.Closure(engine.Selection(manifest, strict: false));
        var requiredBy = RequiredByIndex(remaining, indexByKey);
        var blocked = removed
            .Select(entry => (Entry: entry, Who: requiredBy.GetValueOrDefault((entry.Kind, entry.Id)) ?? []))
            .Where(pair => remaining.Contains((pair.Entry.Kind, pair.Entry.Id)) || pair.Who.Count > 0)
            .ToList();
        if (blocked.Count == 0)
        {
            return;
        }

        var detail = string.Join(
            "; ",
            blocked.Select(pair =>
                $"{ItemId.Display(pair.Entry.Id)} ({pair.Entry.Kind}) is required by "
                + $"{string.Join(", ", pair.Who.OrderBy(name => name, StringComparer.Ordinal).Select(ItemId.Display).DefaultIfEmpty("another selected item"))}"));
        var forced = blocked.Any(pair => pair.Entry.Via == ResolverDefaults.ExcludeKey)
            ? "the dependent will be installed without it"
            : "a later sync will restore it";
        throw new ResolverException(
            $"refusing to orphan — {detail}. Remove the dependent first, "
            + $"--force to edit the manifest anyway ({forced})");
    }

    /// <summary>Maps each required skill to the ids that declare it.</summary>
    private static Dictionary<(string Kind, string Id), List<string>> RequiredByIndex(
        IEnumerable<(string Kind, string Id)> selection,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey)
    {
        var requiredBy = new Dictionary<(string Kind, string Id), List<string>>();
        foreach (var (kind, itemId) in selection)
        {
            if (!indexByKey.TryGetValue((kind, itemId), out var item))
            {
                continue;
            }

            foreach (var dependency in item.Requires)
            {
                var key = (ResolverDefaults.SkillKind, dependency);
                if (!requiredBy.TryGetValue(key, out var who))
                {
                    who = [];
                    requiredBy[key] = who;
                }

                who.Add(itemId);
            }
        }

        return requiredBy;
    }

    private static void RemoveFromLockedList(ManifestDocument manifest, List<RemovedItem> removed)
    {
        var removedIds = removed.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var locks = manifest.GetStringArray("locked")
            .Where(value => !removedIds.Contains(ItemId.SoftCanonical(value) ?? string.Empty))
            .ToList();
        SetStringArray(manifest, "locked", locks);
    }

    /// <summary>Deletes the lock-recorded files for the removed items.</summary>
    private List<PrunedItem> PruneRemoved(List<RemovedItem> removed)
    {
        var lockDocument = queries.LoadLock();
        var drop = removed.Select(entry => (entry.Kind, entry.Id)).ToHashSet();
        var pruned = new List<PrunedItem>();
        var remaining = new List<LockEntry>();
        foreach (var entry in lockDocument.Entries)
        {
            if (!drop.Contains((entry.Kind, entry.Id)))
            {
                remaining.Add(entry);
                continue;
            }

            var (deleted, markers) = Materializer.PruneEntry(queries.Paths, entry);
            pruned.Add(new PrunedItem(entry.Id, entry.Kind, entry.Path, deleted is not null, markers));
        }

        if (remaining.Count != lockDocument.Entries.Count)
        {
            LockDocument.Save(queries.Paths.LockPath, remaining);
        }

        return pruned;
    }

    public OperationOutcome SetLocked(IReadOnlyList<string> ids, bool locked)
    {
        var manifest = queries.LoadManifest()
            ?? throw new ResolverException(
                $"no manifest at {queries.Paths.ManifestPath} — nothing to lock");
        var index = queries.BuildIndex(manifest);
        var engine = queries.Engine(index);
        var selected = engine.EffectiveSelection(manifest, strict: false)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var entries = manifest.GetStringArray("locked").ToList();
        var current = entries.Select(value => ItemId.SoftCanonical(value) ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var raw in ids)
        {
            var itemId = ItemId.Canonical(raw);
            if (locked)
            {
                AddLock(selected, entries, current, changed, itemId);
            }
            else
            {
                entries = RemoveLock(entries, current, changed, itemId);
            }
        }

        SetStringArray(manifest, "locked", entries);
        manifest.Save();

        var lockedNames = entries
            .Select(value => ItemId.Display(ItemId.SoftCanonical(value)))
            .ToList();
        var payload = new LockPayload(
            locked ? "lock" : "unlock",
            lockedNames,
            changed.Select(ItemId.Display).ToList());
        var lines = new List<string>();
        if (changed.Count > 0)
        {
            lines.Add($"{(locked ? "locked" : "unlocked")}: {string.Join(", ", changed.Select(ItemId.Display))}");
        }
        else
        {
            lines.Add($"nothing to {(locked ? "lock" : "unlock")} — {(locked ? "already locked" : "not locked")}");
        }

        lines.Add(locked
            ? "locked items are never overwritten by update or sync; unlock to resume updates"
            : "unlocked items follow the catalog again on the next update");
        return new OperationOutcome(new CliResult(0, JoinLines(lines), string.Empty), payload);
    }

    private static void AddLock(
        HashSet<string> selected,
        List<string> entries,
        HashSet<string> current,
        List<string> changed,
        string itemId)
    {
        if (!selected.Contains(itemId))
        {
            throw new ResolverException(
                $"'{ItemId.Display(itemId)}' is not selected — lock an item this project installs");
        }

        if (current.Add(itemId))
        {
            entries.Add(ItemId.Display(itemId));
            changed.Add(itemId);
        }
    }

    private static List<string> RemoveLock(
        List<string> entries,
        HashSet<string> current,
        List<string> changed,
        string itemId)
    {
        var kept = entries.Where(value => ItemId.SoftCanonical(value) != itemId).ToList();
        if (kept.Count == entries.Count)
        {
            return entries;
        }

        current.Remove(itemId);
        changed.Add(itemId);
        return kept;
    }

    /// <summary>
    /// Decides what sync does with one selected item: create, update, leave alone or conflict.
    /// Returns the planned action, the conflict message (if any) and the lock entry to record.
    /// </summary>
    private static (SyncAction Action, string? Conflict, LockEntry? Entry) PlanAction(
        CatalogItem item,
        LockEntry entry,
        LockEntry? previous,
        ActionContext context)
    {
        var kind = item.Kind;
        var itemId = item.Id;
        if (context.SameDirectory)
        {
            return (Action(ResolverDefaults.ActionNoOp, kind, itemId, item.Path, "source == target"), null, entry);
        }

        if (!PathOps.ExistsOrLink(context.Target))
        {
            return (Action(ResolverDefaults.ActionCreate, kind, itemId, item.Path), null, entry);
        }

        if (previous is null)
        {
            return (
                Action(ResolverDefaults.ActionConflict, kind, itemId, item.Path, "untracked path exists"),
                $"{item.Path} exists but is not recorded in the lock",
                null);
        }

        if (context.Frozen)
        {
            return (Action(ResolverDefaults.ActionOk, kind, itemId, item.Path, "locked"), null, previous);
        }

        if (WorkspacePaths.SamePath(context.Source, context.Target))
        {
            return (Action(ResolverDefaults.ActionOk, kind, itemId, item.Path), null, entry);
        }

        var current = ContentHash.InstalledHash(item, context.Target);
        var needs = !string.Equals(current, entry.Sha256, StringComparison.Ordinal)
            || !string.Equals(previous.Path, entry.Path, StringComparison.Ordinal)
            || !string.Equals(previous.Materialize ?? "link", context.Mode, StringComparison.Ordinal);
        return (
            Action(needs ? ResolverDefaults.ActionUpdate : ResolverDefaults.ActionOk, kind, itemId, item.Path),
            null,
            entry);
    }

    /// <summary>The per-item inputs <see cref="PlanAction"/> needs that do not change between items.</summary>
    private sealed record ActionContext(string Source, string Target, bool Frozen, bool SameDirectory, string Mode);

    /// <summary>Splits lock entries that are no longer selected into prunable and unknown kinds.</summary>
    private static (List<LockEntry> PendingPrune, List<LockEntry> PreservedUnknown) PartitionLocked(
        IReadOnlyDictionary<(string Kind, string Id), LockEntry> locked,
        HashSet<(string Kind, string Id)> selectedKeys,
        HashSet<string> knownKinds)
    {
        var pendingPrune = new List<LockEntry>();
        var preservedUnknown = new List<LockEntry>();
        foreach (var (_, entry) in locked)
        {
            if (selectedKeys.Contains((entry.Kind, entry.Id)))
            {
                continue;
            }

            if (knownKinds.Contains(entry.Kind))
            {
                pendingPrune.Add(entry);
            }
            else
            {
                preservedUnknown.Add(entry);
            }
        }

        return (pendingPrune, preservedUnknown);
    }

    public SyncOutcome Sync(
        bool check = false,
        bool frozen = false,
        bool prune = false,
        bool dryRun = false,
        string? materializeOverride = null)
    {
        var plan = PlanSync(prune, materializeOverride);

        if (check)
        {
            return CheckOutcome(plan);
        }

        if (frozen && plan.LockChanged)
        {
            return FrozenOutcome(plan);
        }

        var performed = new List<SyncPerformed>();
        var markersRemoved = 0;
        var legacyIndexRemoved = false;
        MirrorReport? mirror = null;
        AgentConfigPayload? agentConfig = null;
        if (!dryRun)
        {
            (markersRemoved, legacyIndexRemoved) = RemoveLegacyArtifacts(plan.MarkerFiles, plan.OldIndex);
            MaterializeChanged(plan.Manifest, plan.IndexByKey, plan.Actions, plan.Mode, performed, plan.Conflicts);
            if (prune)
            {
                PruneEntries(plan.PendingPrune, performed);
            }

            if (plan.LockChanged)
            {
                LockDocument.Save(queries.Paths.LockPath, plan.Entries);
            }

            _ = setup.InstallTooling(plan.TemplateRoot, overwrite: false, plan.Reference);
            agentConfig = AgentConfigPayload.From(setup.InstallAgentConfig(plan.Manifest.Root));
            if (plan.Fresh)
            {
                _ = setup.InstallScaffold(plan.TemplateRoot);
            }

            _ = setup.EnsureGitignore();
            mirror = new PlatformMirrorService(queries.Paths).Sync(plan.Manifest);
            _ = new InstructionDocumentService(queries).Update(plan.Manifest.Root);
        }

        if (mirror is not null)
        {
            plan.Conflicts.AddRange(mirror.Conflicts.Select(target =>
                $"{target} holds content the sync will not overwrite; resolve it by hand, then sync again"));
        }

        var applied = new SyncReport(
            dryRun ? "dry-run" : "apply",
            false,
            plan.SameDirectory,
            plan.Actions,
            plan.PendingPrune.Select(entry => entry.Path).ToList(),
            plan.PreservedUnknown.Select(entry => entry.Id).ToList(),
            plan.MarkerFiles.Select(Relative).ToList(),
            plan.OldIndex is null ? null : Relative(plan.OldIndex),
            plan.LockChanged,
            performed,
            markersRemoved,
            legacyIndexRemoved,
            prune,
            plan.Conflicts,
            plan.Dependencies);

        return new SyncOutcome(
            new CliResult(
                plan.Conflicts.Count > 0 ? 1 : 0,
                JoinLines(DescribeOutcome(plan, agentConfig, mirror, prune, dryRun)),
                JoinLines(plan.Conflicts.Select(conflict => $"CONFLICT: {conflict}"))),
            applied);
    }

    /// <summary>Everything sync decides before it writes anything.</summary>
    private sealed record SyncPlan(
        bool Fresh,
        ManifestDocument Manifest,
        string Mode,
        string TemplateRoot,
        bool SameDirectory,
        string Reference,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> IndexByKey,
        List<SyncAction> Actions,
        List<string> Conflicts,
        List<LockEntry> Entries,
        List<string> MarkerFiles,
        string? OldIndex,
        List<LockEntry> PendingPrune,
        List<LockEntry> PreservedUnknown,
        bool LockChanged,
        bool Drift,
        List<RequiredItem> Dependencies);

    /// <summary>Reads the manifest and catalog and works out what sync would do, without writing.</summary>
    private SyncPlan PlanSync(bool prune, string? materializeOverride)
    {
        var existing = queries.LoadManifest();
        var manifest = existing ?? queries.StarterManifest();
        var mode = WorkspaceQueries.MaterializeMode(manifest, materializeOverride);
        var templateRoot = queries.Sources.TemplateRoot(manifest);
        var sameDirectory = WorkspacePaths.SamePath(templateRoot, queries.Paths.Root);
        var index = queries.BuildIndex(manifest);
        var indexByKey = index.ByKey();
        var engine = queries.Engine(index);
        // A repository can carry skills that were added by hand: they are not part of any
        // declared source, but they are already on disk and are intentionally in the manifest.
        // Keep them out of the resolver instead of failing the whole sync when another item is
        // installed; only an id with no local files is treated as a typo.
        var selected = engine.EffectiveSelection(manifest, strict: false);
        var dependencies = engine
            .WithDependencies(engine.Selection(manifest, strict: false))
            .Where(entry => entry.RequiredBy is not null)
            .Select(entry => new RequiredItem(entry.Id, entry.Kind, ItemId.Display(entry.RequiredBy!)))
            .ToList();
        EnsureSelectedItemsExist(indexByKey, selected);
        engine.CheckHookGate(manifest, selected, queries.Sources, queries.Paths);

        var lockDocument = queries.LoadLock();
        var locked = lockDocument.ByKey();
        var frozenIds = engine.LockedIds(manifest);

        var collector = new SyncCollector();
        PlanSyncActions(
            manifest,
            indexByKey,
            selected,
            new SyncOptions(locked, frozenIds, mode, sameDirectory),
            collector);

        var markerFiles = sameDirectory ? [] : Materializer.LegacyMarkers(queries.Paths, collector.Targets);
        var oldIndex = sameDirectory ? null : Materializer.LegacyIndex(queries.Paths);

        var selectedKeys = selected.ToHashSet();
        var knownKinds = ResolverDefaults.Kinds.ToHashSet(StringComparer.Ordinal);
        var (pendingPrune, preservedUnknown) = PartitionLocked(locked, selectedKeys, knownKinds);
        pendingPrune.Sort((left, right) =>
        {
            var compared = string.CompareOrdinal(left.Kind, right.Kind);
            return compared != 0 ? compared : string.CompareOrdinal(left.Id, right.Id);
        });

        var entries = collector.Entries;
        if (!prune)
        {
            entries.AddRange(pendingPrune);
        }

        entries.AddRange(preservedUnknown);

        var newLock = LockDocument.FromEntries(entries);
        var lockChanged = !sameDirectory
            && !string.Equals(LockDocument.Signature(newLock), LockDocument.Signature(lockDocument), StringComparison.Ordinal);
        var changes = collector.Actions.Where(action => action.Action is ResolverDefaults.ActionCreate or ResolverDefaults.ActionUpdate or ResolverDefaults.ActionConflict).ToList();
        var drift = changes.Count > 0
            || pendingPrune.Count > 0
            || lockChanged
            || markerFiles.Count > 0
            || oldIndex is not null;

        return new SyncPlan(
            existing is null,
            manifest,
            mode,
            templateRoot,
            sameDirectory,
            SourceCatalog.SourceRef(manifest),
            indexByKey,
            collector.Actions,
            collector.Conflicts,
            entries,
            markerFiles,
            oldIndex,
            pendingPrune,
            preservedUnknown,
            lockChanged,
            drift,
            dependencies);
    }

    /// <summary>Fails with the missing path when a selected id is neither in the catalog nor on disk.</summary>
    private void EnsureSelectedItemsExist(
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey,
        IEnumerable<(string Kind, string Id)> selected)
    {
        foreach (var (kind, itemId) in selected)
        {
            if (indexByKey.ContainsKey((kind, itemId)))
            {
                continue;
            }

            var target = queries.Paths.TargetPath(kind, ItemId.Split(itemId).Name);
            if (!PathOps.ExistsOrLink(target))
            {
                throw new ResolverException(
                    $"unknown id '{ItemId.Display(itemId)}' — not in the catalog "
                    + "(run 'catalog.py index' if the tree changed)");
            }
        }
    }

    /// <summary>Fills the planned actions, conflicts, lock entries and installed targets for one sync.</summary>
    private void PlanSyncActions(
        ManifestDocument manifest,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey,
        IEnumerable<(string Kind, string Id)> selected,
        SyncOptions options,
        SyncCollector collector)
    {
        foreach (var (kind, itemId) in selected)
        {
            if (!indexByKey.TryGetValue((kind, itemId), out var item))
            {
                continue;
            }

            var target = queries.Paths.TargetPath(kind, item.Name);
            collector.Targets.Add(target);
            var itemRoot = queries.Sources.SourceRoot(item.Source, SourceCatalog.EntryNamed(manifest, item.Source));
            var source = Path.Combine(itemRoot, item.SourcePath ?? item.Path);
            var entry = LockEntry.Make(kind, item, options.Mode);
            var previous = options.Locked.GetValueOrDefault((kind, itemId));

            var context = new ActionContext(
                source,
                target,
                options.FrozenIds.Contains(itemId),
                options.SameDirectory,
                options.Mode);
            var (action, conflict, lockEntry) = PlanAction(item, entry, previous, context);
            collector.Actions.Add(action);
            if (conflict is not null)
            {
                collector.Conflicts.Add(conflict);
            }

            if (lockEntry is not null)
            {
                collector.Entries.Add(lockEntry);
            }
        }
    }

    /// <summary>The settings every item in one sync is planned against.</summary>
    private sealed record SyncOptions(
        IReadOnlyDictionary<(string Kind, string Id), LockEntry> Locked,
        HashSet<string> FrozenIds,
        string Mode,
        bool SameDirectory);

    /// <summary>The lists one sync plan accumulates while it walks the selection.</summary>
    private sealed class SyncCollector
    {
        public List<SyncAction> Actions { get; } = [];

        public List<string> Conflicts { get; } = [];

        public List<LockEntry> Entries { get; } = [];

        public List<string> Targets { get; } = [];
    }

    /// <summary>Deletes the pre-materialize marker files and legacy index; returns how many went.</summary>
    private (int MarkersRemoved, bool LegacyIndexRemoved) RemoveLegacyArtifacts(
        List<string> markerFiles,
        string? oldIndex)
    {
        var removed = 0;
        foreach (var marker in markerFiles)
        {
            try
            {
                File.Delete(marker);
                removed++;
            }
            catch (IOException)
            {
                // already gone with a directory replaced by materialize
            }
        }

        var legacyIndexRemoved = false;
        if (oldIndex is not null)
        {
            try
            {
                File.Delete(oldIndex);
                legacyIndexRemoved = true;
            }
            catch (IOException)
            {
                // best effort
            }
        }

        if (markerFiles.Count > 0
            && Directory.Exists(queries.Paths.InstalledDirectory)
            && !Directory.EnumerateFileSystemEntries(queries.Paths.InstalledDirectory).Any())
        {
            Directory.Delete(queries.Paths.InstalledDirectory);
        }

        return (removed, legacyIndexRemoved);
    }

    /// <summary>Writes every item sync decided to create or update, recording what happened.</summary>
    private void MaterializeChanged(
        ManifestDocument manifest,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey,
        List<SyncAction> actions,
        string mode,
        List<SyncPerformed> performed,
        List<string> conflicts)
    {
        foreach (var action in actions.Where(action => action.Action is ResolverDefaults.ActionCreate or ResolverDefaults.ActionUpdate))
        {
            if (!indexByKey.TryGetValue((action.Kind, action.Id), out var item))
            {
                continue;
            }

            var target = queries.Paths.TargetPath(action.Kind, item.Name);
            var itemRoot = queries.Sources.SourceRoot(
                item.Source,
                SourceCatalog.EntryNamed(manifest, item.Source));
            var source = Path.Combine(itemRoot, item.SourcePath ?? item.Path);
            var ok = Materializer.MaterializeItem(item, source, target, mode);
            performed.Add(new SyncPerformed(action.Action, action.Kind, action.Id, action.Path, ok));
            if (!ok)
            {
                conflicts.Add($"could not materialize {action.Path}");
            }
        }
    }

    private void PruneEntries(IEnumerable<LockEntry> pendingPrune, List<SyncPerformed> performed)
    {
        foreach (var entry in pendingPrune)
        {
            var (deleted, markers) = Materializer.PruneEntry(queries.Paths, entry);
            performed.Add(new SyncPerformed(
                "delete",
                entry.Kind,
                entry.Id,
                entry.Path,
                true,
                deleted is not null,
                markers));
        }
    }

    private SyncOutcome CheckOutcome(SyncPlan plan)
    {
        var report = new SyncReport(
            "check",
            !plan.Drift,
            plan.SameDirectory,
            plan.Actions,
            plan.PendingPrune.Select(entry => entry.Path).ToList(),
            plan.PreservedUnknown.Select(entry => entry.Id).ToList(),
            plan.MarkerFiles.Select(Relative).ToList(),
            plan.OldIndex is null ? null : Relative(plan.OldIndex),
            plan.LockChanged,
            [],
            0,
            false,
            false,
            plan.Conflicts,
            plan.Dependencies);

        var lines = new List<string>();
        if (plan.SameDirectory)
        {
            lines.Add("no-op: source == target");
        }

        lines.AddRange(Describe(plan.Actions, plan.PendingPrune));
        lines.Add(plan.Drift ? "drift detected" : "in sync");
        return new SyncOutcome(new CliResult(plan.Drift ? 1 : 0, JoinLines(lines), string.Empty), report);
    }

    private SyncOutcome FrozenOutcome(SyncPlan plan)
    {
        var report = new SyncReport(
            "frozen",
            false,
            plan.SameDirectory,
            plan.Actions,
            plan.PendingPrune.Select(entry => entry.Path).ToList(),
            plan.PreservedUnknown.Select(entry => entry.Id).ToList(),
            [],
            null,
            true,
            [],
            0,
            false,
            false,
            plan.Conflicts,
            plan.Dependencies);
        var lines = Describe(plan.Actions, plan.PendingPrune)
            .Append($"frozen lock would need to change: {queries.Paths.LockPath}");
        return new SyncOutcome(new CliResult(1, JoinLines(lines), string.Empty), report);
    }

    private static List<string> DescribeOutcome(
        SyncPlan plan,
        AgentConfigPayload? agentConfig,
        MirrorReport? mirror,
        bool prune,
        bool dryRun)
    {
        var output = new List<string>();
        var prefix = dryRun ? "[DRY RUN] " : string.Empty;
        output.AddRange(Describe(plan.Actions, plan.PendingPrune));
        if (agentConfig is not null)
        {
            output.AddRange(agentConfig.Lines());
        }

        if (mirror is not null)
        {
            output.AddRange(mirror.Lines);
        }

        if (plan.PendingPrune.Count > 0 && !prune)
        {
            output.Add($"{prefix}{plan.PendingPrune.Count} item(s) pending prune (run 'catalog.py sync --prune')");
        }

        var touched = plan.Actions.Count(action => action.Action is ResolverDefaults.ActionCreate or ResolverDefaults.ActionUpdate);
        if (dryRun)
        {
            output.Add($"{prefix}{touched} item(s) would change; nothing written");
            return output;
        }

        var lockState = plan.LockChanged ? "lock updated" : "lock unchanged";
        output.Add($"{prefix}{touched} item(s) changed; {lockState}");
        return output;
    }

    public OperationOutcome ApplyUpdates(
        IReadOnlyCollection<string>? ids = null,
        bool force = false,
        bool tooling = false)
    {
        var plan = queries.PlanUpdates(ids, force);
        var entries = plan.Lock.ByKey().ToDictionary(pair => pair.Key, pair => pair.Value);
        var applied = new List<UpdateAppliedItem>();
        foreach (var row in plan.Chosen)
        {
            if (!plan.IndexByKey.TryGetValue((row.Kind, row.Id), out var item))
            {
                continue;
            }

            var root = queries.Sources.SourceRoot(
                item.Source,
                SourceCatalog.EntryNamed(plan.Manifest, item.Source));
            var target = queries.Paths.TargetPath(row.Kind, item.Name);
            var source = Path.Combine(root, item.SourcePath ?? item.Path);
            var ok = Materializer.MaterializeItem(item, source, target, plan.Mode);
            applied.Add(new UpdateAppliedItem(row.Id, ok, row.Status));
            if (ok)
            {
                entries[(row.Kind, row.Id)] = LockEntry.Make(row.Kind, item, plan.Mode);
            }
        }

        var templateRoot = queries.Sources.TemplateRoot(plan.Manifest);
        if (!WorkspacePaths.SamePath(templateRoot, queries.Paths.Root))
        {
            LockDocument.Save(queries.Paths.LockPath, entries.Values);
        }

        var agentLines = new List<string>();
        if (tooling)
        {
            _ = setup.MigrateCatalogDirectory();
            var sourceRoot = queries.Sources.TemplateRoot(plan.Manifest);
            _ = setup.InstallTooling(sourceRoot, overwrite: true, SourceCatalog.SourceRef(plan.Manifest));
            agentLines.AddRange(AgentConfigPayload.From(setup.InstallAgentConfig(plan.Manifest.Root)).Lines());
            _ = setup.EnsureGitignore();
            _ = new PlatformMirrorService(queries.Paths).Sync(plan.Manifest);
        }

        _ = new InstructionDocumentService(queries).Update(plan.Manifest.Root);

        var payload = new UpdateApplyPayload(
            true,
            false,
            applied,
            plan.Blocked,
            plan.Held,
            plan.RemoteSelected);
        var lines = new List<string>(agentLines);
        lines.AddRange(applied.Select(entry => $"  {(entry.Ok ? "OK" : "FAILED"),-8} {ItemId.Display(entry.Id)}"));
        lines.AddRange(plan.Held.Select(row => $"  {"LOCKED",-8} {row.DisplayId} ({row.Kind}) — left untouched"));
        lines.Add($"\n{applied.Count} item(s) updated, {plan.Held.Count} locked and left untouched.");
        return new OperationOutcome(
            new CliResult(applied.All(entry => entry.Ok) ? 0 : 1, JoinLines(lines), string.Empty),
            payload);
    }

    public OperationOutcome Pin(string? release = null, string? repo = null)
    {
        var existing = queries.LoadManifest();
        var manifest = existing ?? queries.StarterManifest().Bind(queries.Paths.ManifestPath);
        var sources = manifest.GetObject("sources") ?? new JsonObject();
        if (manifest.GetObject("sources") is null)
        {
            manifest.Set("sources", sources);
        }

        var entry = sources[ResolverDefaults.TemplateSource] is JsonObject template
            ? (JsonObject)template.DeepClone()
            : new JsonObject();
        var tag = release;
        if (string.IsNullOrWhiteSpace(tag))
        {
            var row = queries.ReleaseRow(ResolverDefaults.TemplateSource, entry);
            tag = row.Latest;
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ResolverException(
                    $"no release to pin {ResolverDefaults.TemplateSource} to: "
                    + $"{row.Error ?? "no semantic-version tags were found"}");
            }
        }

        var index = queries.BuildIndex(manifest);
        var indexByKey = index.ByKey();
        var engine = queries.Engine(index);
        var selected = engine.EffectiveSelection(manifest, strict: false);
        var hasExistingSelection = ResolverDefaults.Kinds.Any(kind =>
            manifest.GetStringArray(ResolverDefaults.ManifestKeys[kind]).Count > 0);
        var hasLockEntries = queries.LoadLock().Entries.Count > 0;
        if (selected.Count == 0 && (hasExistingSelection || hasLockEntries))
        {
            throw new ResolverException(
                "refusing to pin with an empty selection — the catalog did not resolve the "
                + "items this repository already has; run 'catalog.py index' and check the manifest");
        }

        var wasLocalTree = WorkspacePaths.SamePath(queries.Sources.TemplateRoot(manifest), queries.Paths.Root);
        var lists = ResolverDefaults.Kinds.ToDictionary(
            kind => kind,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var (kind, itemId) in selected)
        {
            if (!indexByKey.TryGetValue((kind, itemId), out var item)
                || item.Source != ResolverDefaults.TemplateSource)
            {
                continue;
            }

            lists[kind].Add(ItemId.Display(itemId));
        }

        entry["repo"] = repo ?? (entry["repo"] is JsonValue repoValue && repoValue.TryGetValue<string>(out var repoText)
            ? repoText
            : ResolverDefaults.TemplateRepo);
        entry["ref"] = tag;
        entry["resolve"] = "remote";
        entry.Remove("path");
        sources[ResolverDefaults.TemplateSource] = entry;
        foreach (var (kind, values) in lists)
        {
            SetStringArray(manifest, ResolverDefaults.ManifestKeys[kind], values);
        }

        manifest.Remove("preset");
        manifest.Remove("presets");
        if (wasLocalTree)
        {
            manifest.Set("materialize", "copy");
        }

        manifest.Save();
        if (wasLocalTree)
        {
            // Wave F: seed the tooling record from the files this tree already carries.
        }

        var payload = new PinPayload(
            ResolverDefaults.TemplateSource,
            entry["repo"]!.GetValue<string>(),
            tag,
            lists.Values.Sum(values => values.Count),
            queries.Paths.ManifestPath);
        return new OperationOutcome(
            new CliResult(
                0,
                $"pinned {ResolverDefaults.TemplateSource} to {tag} ({payload.Repo}) — "
                + $"{payload.Selected} selected item(s)\n"
                + "run 'catalog.py update --apply' to replace them",
                string.Empty),
            payload);
    }

    public OperationOutcome Preset(string action, IReadOnlyList<string> names)
    {
        var adding = action == "add";
        var existing = queries.LoadManifest();
        if (existing is null && !adding)
        {
            throw new ResolverException(
                $"no manifest at {queries.Paths.ManifestPath} — nothing to remove");
        }

        var manifest = existing ?? queries.StarterManifest().Bind(queries.Paths.ManifestPath);
        var index = queries.BuildIndex(manifest);
        var engine = queries.Engine(index);
        var current = SelectionEngine.PresetNames(manifest).ToList();
        var requested = engine.ValidatePresetNames(names);
        var changed = new List<string>();
        if (adding)
        {
            foreach (var name in requested.Where(name => !current.Contains(name)))
            {
                current.Add(name);
                changed.Add(name);
            }
        }
        else
        {
            foreach (var name in requested.Where(current.Contains))
            {
                current.Remove(name);
                changed.Add(name);
            }
        }

        SetStringArray(manifest, "presets", current);
        manifest.Remove("preset");
        manifest.Save();

        var payload = new PresetMutationPayload(action, changed, current, queries.Paths.ManifestPath);
        return new OperationOutcome(
            new CliResult(
                0,
                $"{(adding ? "added" : "removed")} preset(s): {(changed.Count > 0 ? string.Join(", ", changed) : "(none)")}\n"
                + $"presets: {(current.Count > 0 ? string.Join(", ", current) : "(none)")}",
                string.Empty),
            payload);
    }

    // ── one RPC per user-level operation ────────────────────────────────────

    public OperationOutcome Install(IReadOnlyList<string> ids)
    {
        var added = Add(ids);
        if (added.Result.Code != 0)
        {
            return added;
        }

        // The dependencies are what `Add` selected on top of the request. Sync sees them as
        // already selected, so its own report has nothing to say about them.
        var dependencies = (added.Payload as AddPayload)?.Dependencies ?? [];
        var sync = Sync();
        return new OperationOutcome(
            sync.Result,
            new InstallPayload(sync.Result, dependencies, sync.Report.Actions));
    }

    public OperationOutcome RemoveSelection(IReadOnlyList<string> ids, bool prune)
    {
        var removed = Remove(ids, prune: prune);
        if (removed.Result.Code != 0)
        {
            return removed;
        }

        var sync = Sync(prune: prune);
        return new OperationOutcome(sync.Result, sync.Report);
    }

    public OperationOutcome AddPresetAndSync(string name)
    {
        var preset = Preset("add", [name]);
        if (preset.Result.Code != 0)
        {
            return preset;
        }

        var sync = Sync();
        return new OperationOutcome(sync.Result, sync.Report);
    }

    public OperationOutcome PruneDeselected()
    {
        var sync = Sync(prune: true);
        return new OperationOutcome(sync.Result, sync.Report);
    }

    public OperationOutcome Upgrade(string release)
    {
        var pinned = Pin(release);
        if (pinned.Result.Code != 0)
        {
            return pinned;
        }

        return ApplyUpdates(force: false, tooling: true);
    }

    private static (List<(string Kind, string Id)> Resolved, List<string> Problems) ResolveAddTargets(
        IReadOnlyList<string> rawIds,
        string? kindFlag,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey)
    {
        var resolved = new List<(string Kind, string Id)>();
        var problems = new List<string>();
        foreach (var raw in rawIds)
        {
            var itemId = ItemId.Canonical(raw);
            var matches = ResolverDefaults.Kinds.Where(kind => indexByKey.ContainsKey((kind, itemId))).ToList();
            var target = ResolveAddTarget(raw, itemId, matches, kindFlag, indexByKey);
            if (target.Problem is not null)
            {
                problems.Add(target.Problem);
            }
            else if (!resolved.Contains(target.Key))
            {
                resolved.Add(target.Key);
            }
        }

        return (resolved, problems);
    }

    /// <summary>Resolves one add argument to a catalog key, or to the problem that blocks it.</summary>
    private static ((string Kind, string Id) Key, string? Problem) ResolveAddTarget(
        string raw,
        string itemId,
        List<string> matches,
        string? kindFlag,
        IReadOnlyDictionary<(string Kind, string Id), CatalogItem> indexByKey)
    {
        if (!string.IsNullOrEmpty(kindFlag))
        {
            if (indexByKey.ContainsKey((kindFlag, itemId)))
            {
                return ((kindFlag, itemId), null);
            }

            var problem = matches.Count > 0
                ? $"'{ItemId.Display(itemId)}' is not a {kindFlag} (it is a {matches[0]})"
                : UnknownMessage(raw);
            return (default, problem);
        }

        return matches.Count switch
        {
            0 => (default, UnknownMessage(raw)),
            > 1 => (default,
                $"'{ItemId.Display(itemId)}' exists as both a skill and an agent — "
                + "pass --kind skill or --kind agent"),
            _ => ((matches[0], itemId), null),
        };
    }

    private static string UnknownMessage(string raw) =>
        $"unknown id '{raw}': not in the catalog";

    private static SyncAction Action(string name, string kind, string itemId, string path, string detail = "") =>
        new(name, kind, itemId, path, detail);

    private static IEnumerable<string> Describe(IEnumerable<SyncAction> actions, IEnumerable<LockEntry> pendingPrune)
    {
        foreach (var action in actions)
        {
            var detail = string.IsNullOrEmpty(action.Detail) ? string.Empty : $" ({action.Detail})";
            yield return $"  {action.Action.ToUpperInvariant(),-8} {action.Path}{detail}";
        }

        foreach (var entry in pendingPrune)
        {
            yield return $"  {"PRUNE",-8} {entry.Path} (not selected; needs --prune)";
        }
    }

    private string Relative(string path) =>
        Path.GetRelativePath(queries.Paths.Root, path).Replace('\\', '/');

    private static string JoinLines(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        return text.Length == 0 ? string.Empty : text + "\n";
    }

    private static void SetStringArray(ManifestDocument manifest, string key, IEnumerable<string> values)
    {
        manifest.Set(key, new JsonArray(values.Select(value => (JsonNode)value).ToArray()));
        _ = manifest;
    }

    private static string? ToStringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
