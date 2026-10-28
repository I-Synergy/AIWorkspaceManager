# Plan: AI Manager + Per-Repo Selection

**Status:** APPROVED 2026-10-27 — Phase 0 executing. Progress: `.ai/progress/ai-manager-selection.md`.
**Date:** 2026-10-27
**Slug:** `ai-manager-selection`
**Branch:** `feature/ai-manager-selection`
**Requested by:** User — merge this template's skills/agents with `github/awesome-copilot`, select per repo, manage updates, possibly via a VS Code extension.

---

## Decisions Settled With The User (2026-10-27)

| Question | Answer |
|---|---|
| Delivery vehicle | **Manifest + CLI resolver first**, VS Code extension later as a thin UI over it. |
| Catalog merge strategy | **Reference upstream by pinned ref; never fork content.** Lock file pins refs and hashes. |
| CLI packaging | **`.ai/scripts/catalog.py`**, stdlib + PyYAML only, matching the existing script convention. It stays in `TEMPLATE_OWNED` so downstream projects get the resolver. |
| Extension placement | **This repo, `extensions/ai-manager/`** — outside the copy surface, never added to `TEMPLATE_OWNED`. Rationale in *Placement* below. |
| Presets (Phase 1) | `minimal`, `dotnet-web`, `maui-mobile`, `blazor`, `full`. `full` is today's 80 skills + 9 agents and doubles as the regression guard. |
| Prune semantics | `remove` edits the manifest only; `sync --prune` deletes; paths the lock does not record are never touched. |
| Extension distribution | Internal `.vsix` attached to the same GitHub Release as the template tag. Marketplace deferred. |

## Council

Does not convene at plan stage: both forks above were put to the user as options and answered with the trade-offs stated.

Re-examine before Phase 2 ships. Introducing third-party agents/skills into every downstream project is a supply-chain surface change (executable-ish instructions, MCP server references, `hooks/` entries). That is the "alters the exposure surface" trigger. Phase 2 must land with provenance metadata, a `--check`-able lock, and an explicit trust note, or go to a council first.

---

## Problem

The template currently has no notion of *which* skills a project wants. `.ai/skills/` (80) and `.ai/agents/` (9) are copied wholesale by `upgrade-template.py` (`TEMPLATE_OWNED` includes both directories), and `sync-skills.py` junctions the whole folder into `.claude/`, `.github/`, `.reasonix/`. Selection is all-or-nothing by construction.

Merging `github/awesome-copilot` at `main` adds 222 agents, 193 instructions, 447 skills, 108 plugins, 8 hooks, 8 workflows — a merged catalog of roughly 530 skills and 230 agents. The cost is paid on three independent budgets:

1. **Files** — ~730 directories × 3 platform mirrors.
2. **Model context** — every installed skill's `name`+`description` is injected up front, every turn. This session's own prompt already lists 102 skills.
3. **Maintenance** — 8 validators in `.github/workflows/validate-template.yml` run over every skill/agent; `upgrade-template.py` diffs every template-owned file on upgrade.

The installed set must be small and explicit; the *available* set can be large.

## Non-Goals

- Re-implementing the Copilot plugin marketplace (`copilot plugin install <name>@awesome-copilot`). That is user-scoped, not repo-scoped, and stays untouched.
- Editing or normalising upstream content. Resolution and materialisation only.
- Publishing the VS Code extension to the Marketplace in this plan (Phase 3 ships a `.vsix`).
- Replacing the junction mechanism. Folder-level junctions stay exactly as they are; selection changes **what is inside** `.ai/skills/` and `.ai/agents/`.

---

## Design

### Layers

```
catalog/                      generated index over N sources (committed in the template)
  index.json                  id, source, ref, kind, path, description, sha256, license, upstream-url
  presets.json                named bundles: minimal, dotnet-web, maui-mobile, blazor, ...
.ai/ai.json                   PROJECT-OWNED selection (committed downstream, reviewable in PRs)
.ai/ai.lock.json              resolved refs + per-item sha256 (committed)
.ai/scripts/catalog.py        the resolver CLI — stdlib + PyYAML only
```

The CLI is the contract. Everything else — CI, Claude Code, Copilot CLI, devcontainers, and later the extension — is a consumer.

### `.ai/ai.json` (selection, hand-editable)

```jsonc
{
  "$schema": "../catalog/ai.schema.json",
  "version": 1,
  "preset": "dotnet-web",
  "sources": {
    "template":        { "repo": "I-Synergy/AI",           "ref": "v1.5.0" },
    "awesome-copilot": { "repo": "github/awesome-copilot", "ref": "9f3c1ab" }
  },
  "skills":      ["dotnet-engineer", "council"],
  "agents":      ["architect", "developer"],
  "instructions":["awesome-copilot:dotnet.instructions.md"],
  "materialize": "link"
}
```

- Ids are namespaced `source:id`; the `template` namespace may be written bare (`dotnet-engineer`) for readability.
- `preset` is expanded first, then the explicit lists are unioned on top. Removing an item that a preset brings in requires `"exclude": []`.
- `materialize`: `link` (per-item junction/symlink) or `copy` (for contributors and CI without symlink rights).

### `.ai/ai.lock.json`

```jsonc
{
  "lockVersion": 1,
  "resolved": [
    { "id": "template:dotnet-engineer", "ref": "v1.5.0", "path": ".ai/skills/dotnet-engineer",
      "sha256": "…", "files": 7, "license": "MIT", "upstream": "https://github.com/I-Synergy/AI/tree/v1.5.0" }
  ]
}
```

The lock is the only thing upgrade logic trusts. Hashes cover the whole directory tree (sorted path + content), so a locally edited file is detectable without diffing against upstream.

### CLI surface

All commands accept `--json` and never prompt unless `--interactive` is passed.

| Command | Purpose |
|---|---|
| `catalog.py index` | Build/refresh `catalog/index.json` from local `.ai/` (offline) or from sources in the manifest (Phase 2). |
| `catalog.py add <id…> [--preset p]` | Add to `.ai/ai.json`, resolve, materialise. |
| `catalog.py remove <id…> [--prune]` | Remove from manifest; `--prune` also deletes the materialised files. |
| `catalog.py sync [--check] [--frozen]` | Reconcile disk with manifest+lock. `--check` exits non-zero on drift (CI). `--frozen` refuses to update the lock. |
| `catalog.py status [--json]` | Per item: `ok`, `not-installed`, `missing`, `upstream-changed`, `local-modified`, `both`, `removed-upstream`. |
| `catalog.py update [--apply] [--only id…]` | Refresh lock to newer pinned refs, show three-way status. Refuses to overwrite `local-modified` without `--force`. |
| `catalog.py list [--installed] [--available] [--json]` | Discovery. |

`status --json` is the extension's entire data source. `sync --check` is what CI runs.

### Materialisation

- `link`: create a per-item junction on Windows (`mklink /J`, no admin required) or a relative symlink elsewhere, reusing `_create_link`/`_rmdir` from `sync-skills.py`. Folder-level junctions to `.cli/`… → unchanged, they now see only the selected items.
- `copy`: plain directory copy with the lock hash as the marker.
- **Prune safety:** `sync` only ever touches paths recorded in the lock. Untracked files under `.ai/skills/` are never deleted. Deleting requires `--prune`.
- A generated header (`.ai-installed.json` per item) carries `id`, `ref`, `sha256`, `materialize` so a half-finished clone is recoverable.

### Context reduction (the budget that actually hurts)

With 530 available and ~15 installed, the per-turn skill listing stays small — but only if discovery does not enumerate everything. Phase 4 adds `catalog.py index --compact`, which writes a single `.ai/skills/INDEX.md` (one line per *available* item, generated, gitignored) plus a short "how to add a skill" note in `AGENTS.md`. Agents search that file instead of receiving 530 descriptions.

### Integration with existing tooling

| Existing | Change |
|---|---|
| `upgrade-template.py` | Stop copying `.ai/skills`/`.ai/agents` wholesale. Replace with a manifest reconcile that reuses the existing ownership tables. Keep `--skills-only` as an alias for `sync --skills`. |
| `sync-skills.py` | Untouched. Still creates folder-level junctions three ways. |
| `.github/workflows/validate-template.yml` | Add `catalog.py sync --check` and `catalog.py index --verify`. Existing validators run over installed items only, which is the point. |
| `.ai/tests/` | New `test_catalog.py` (pytest, already wired via `pytest.ini`). |
| `README.md` / `TEMPLATE-USAGE.md` | New "Selecting skills" section; regenerate the skill list from the catalog instead of hand-maintaining it. |

---

## Phases

### Phase 0 — Manifest + local resolver (offline, no network) — ~1–2 days
- `catalog/ai.schema.json`, `catalog/index.schema.json`, `catalog/lock.schema.json`.
- `catalog.py index` over the local `.ai/` tree.
- `add` / `remove` / `list` / `sync` / `status` with `--json`.
- `link` and `copy` materialisation, prune safety, per-item marker.
- Tests: `test_catalog.py` — round-trip add/remove, prune refuses untracked, `sync --check` drift, hash stability, Windows junction + POSIX symlink paths (monkeypatched).

**Exit criterion:** a scratch repo with `ai.json` listing 3 skills gets exactly 3 junctions and `sync --check` is clean; adding a 4th and running `sync --check` fails until sync.

### Phase 1 — Presets + upgrade integration — ~1 day
- `catalog/presets.json` + `--preset` expansion and `exclude`.
- `upgrade-template.py` reconciles via manifest instead of copying both directories.
- CI adds `sync --check`.
- Migrate this repo itself: `ai.json` selecting all 80 today, so the template's own behaviour is unchanged (regression guard).

**Exit criterion:** `python .ai/scripts/upgrade-template.py --dry-run` on a copy of this repo produces zero diffs; `pytest` and the 8 validators stay green.

### Phase 2 — Remote sources, pinned refs, provenance — ~2–3 days
- `git` shallow fetch + sparse checkout of a pinned ref into a cache dir (`$TMPDIR`-like, `.ai/tmp/catalog-cache/`, gitignored).
- Merge multiple sources into one index; namespace collision handling; `instructions/` and `plugins/*/plugin.json` parity with the `agent-plugins.org` 1.0.0 shape already used upstream.
- Index and lock carry `license`, `upstream`, `author`; generate `NOTICE.md` from the installed set.
- Trust: `catalog.py sync` refuses to install `hooks` or `mcp.json` entries without an explicit `"allowHooks": true` in the manifest.

**Council gate:** before this phase ships, decide whether third-party resolution counts as a security-posture change. Default position: it is, and it needs the provenance + allow-list above.

### Phase 3 — VS Code extension (thin) — ~3–5 days
- TypeScript, esbuild, no runtime deps; all logic shells out to `python .ai/scripts/catalog.py … --json`.
- `TreeView` (Installed / Available / Updates), `WebviewPanel` matrix with preset chips + search, `StatusBarItem` for pending updates, `OutputChannel` for sync logs, `FileSystemWatcher` on `.ai/ai.json`.
- Requires workspace trust to write. Commands mirror the CLI. No logic duplication.
- Distribution: `.vsix` + `.vscode/extensions.json` recommendation. Marketplace deferred.

**Exit criterion:** installing a skill from the webview produces the same diff as `catalog.py add` on the CLI, and a repo without the extension behaves identically.

### Phase 4 — Compact index / context budget — ~1 day
Described above; measure before/after with a token count of the injected skill listing.

---

## Risks

| Risk | Mitigation |
|---|---|
| Third-party content as supply chain (`hooks`, `mcp.json`, prompt-injection in skill bodies) | P2 allow-list, provenance in lock, `NOTICE.md`; user reviews the diff of `ai.json` + lock in PRs. |
| Silent skill-name collisions across sources | Namespaced ids mandatory; `index` fails on duplicate resolved paths. |
| Windows junctions not tracked by git | Already the status quo (`sync-skills.py` + hook). Documented in `TEMPLATE-USAGE.md`. |
| `copy` mode drifts from lock | `sync --check` in CI catches it; marker file carries the hash. |
| Destructive prune | Only lock-recorded paths, explicit `--prune`, dry-run by default in the extension UI. |
| Upstream ref disappears / force-push | Lock stores the resolved SHA, not the branch; fetch by SHA with a tag fallback. |
| Two sources of truth for the README skill list | Generate it from `catalog/index.json`. |

## Decisions Needed

1. **CLI packaging** — recommend a new `.ai/scripts/catalog.py`, matching the existing script convention (zero install, invoked by hooks and CI). Alternative: a standalone `ai` package on PATH, nicer UX for the extension and downstream projects, but a new distribution channel.
2. **Preset set** — recommend `minimal`, `dotnet-web`, `maui-mobile`, `blazor`, and `full` (the current 80/9, for this repo's own regression guard).
3. **Extension distribution** — recommend internal `.vsix` for now; Marketplace later if it proves out.
4. **Prune default** — recommend `remove` leaves files, `sync --prune` deletes, extension asks once per session.

## Verification Plan

- `pytest` (new `test_catalog.py` + existing suite) and the 8 workflow validators, locally and in CI.
- Builds, if any are touched: `MSBUILDDISABLENODEREUSE=1` and `-nodeReuse:false`.
- Phase 1 exit criterion doubles as the regression guard for this repo's own 80/9 set.
