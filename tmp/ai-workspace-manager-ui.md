# Plan: AI Workspace Manager — rich UI (NuGet-manager style)

**Status:** IMPLEMENTED — shipped as **AI Workspace Manager for VS Code** in
`extensions/ai-manager/`. Progress: `.ai/progress/ai-manager-ui.md`.
**Date:** 2026-10-27
**Slug:** `ai-manager-ui`
**Requested by:** User — "a rich ui like we did in D:\Projects\Github\NugetPackageManager … not only to install and update skills, agents etc, but also to select the ones we need per project. We should be able to allow to install everything in a new repo."

> **Name:** the extension is **AI Workspace Manager for VS Code**
> (`ai-manager`, publisher `I-Synergy`); the command ids, configuration keys and
> webview identifiers are all `aiManager.*`.

Supersedes the Phase 3 UI in `.ai/plans/ai-manager-selection.md` (that shipped a TreeView +
one checkbox webview; this replaces it with the full-page app).

---

## 1. What the reference actually does (extracted, not guessed)

`D:\Projects\Github\NugetPackageManager` is a 5-view full-page webview app. The parts worth copying
structurally:

| Concern | Reference implementation | Notes for us |
|---|---|---|
| UI stack | **Lit** web components (`src/web/components/*`), `@lit/task` for async render, bundled by **esbuild** | Same. Component-per-view, no framework runtime beyond Lit |
| Host ↔ web | **Typed RPC over `postMessage`**: `RpcHost` (host) + `createRpcClient` returning a `Proxy` typed as `HostAPI` (web), with `Result<T>` and cancellation | Same protocol, our `HostAPI` is much smaller |
| Bundles | `esbuild.js` builds two outputs: `dist/extension.js` (cjs, node, `external: vscode`) and `dist/web.js` (esm, browser) | Same |
| Shell | Full-page `WebviewPanel` with `registerWebviewPanelSerializer` (survives reload), a TreeView launcher, and an update badge | Same, plus drift badge |
| Styling | Only `--vscode-*` CSS variables, `base.css.ts` (scrollbars) + `shared.css.ts` (buttons/spinners/list rows) | Same, so it looks native in every theme |
| Icons | `@vscode/codicons` inlined as dataurl | Same |
| **Dev harness** | `tools/index.html` + `tools/dev-server.mjs` + `tools/mock-api.js`: injects the `--vscode-*` palette, stubs `acquireVsCodeApi`, mounts the real `dist/web.js` | **Same — and this is how the UI gets visually verified here** |
| Tabs | browse / installed / updates / consolidate / vulnerabilities + settings route | Ours: browse / installed / updates / presets + settings |

Design language to match: dense rows (~28 px), `--vscode-list-hoverBackground` hover, right-hand
detail pane, toolbar with search + filters left and primary action right, badge pills for state,
`split.js` for the list/detail split, spinners + skeletons for async.

## 2. The honest gap: what the CLI cannot do yet

A rich UI that *installs and updates* needs three things the resolver does not currently provide.
This is the real cost of the feature, and it is backend work, not UI work.

| Gap | Today | Needed |
|---|---|---|
| **One call to paint the app** | web calls `status --json`, `list --available --json`, … and joins them | `catalog.py browse --json` → available + installed + status + presets + sources in one payload |
| **Presets are invisible** | `presets.json` exists, only used during `_selection` | `catalog.py presets --json` (name, description, resolved counts) and `catalog.py init --preset <name>` for a new repo |
| **"Updates" is inert** | `upstream_changed()` returns `False` always; there is no `update` command; a project with no `path` reads its *own* tree (same_dir), so there is no upstream to compare | Remote resolution + a real `update` |

### 2.1 Remote resolution — the decision that makes Updates real

Currently a source with no `path` reads the project's own tree (`same_dir`): no lock, no upstream,
nothing to update. To have updates, a project must be able to say *"the catalog lives at
`I-Synergy/AI` ref `v1.6.0`; my installed copy is a snapshot of it"*.

Proposed: a per-source `"resolve": "local" | "remote"` (default `local`, i.e. today's behaviour).

```jsonc
"sources": {
  "template":   { "repo": "I-Synergy/AI", "ref": "v1.6.0", "resolve": "remote" },
  "awesome-copilot": { "repo": "github/awesome-copilot", "ref": "9f3c1ab", "resolve": "remote" }
}
```

With `resolve: remote` the resolver fetches the pinned ref into `.ai/tmp/catalog-cache/` (the Phase 2
machinery, unchanged), indexes it, and:

- `sync` materialises from the fetched tree, so the lock is meaningful again;
- `status` can report `upstream-changed` honestly by comparing the fetched item hash with the
  locked hash;
- `update --apply` rewrites the changed items and the lock, refusing `local-modified` without `--force`.

Backwards compatibility: without `resolve`, nothing changes — this repo and every project created
before today keep `local` semantics.

### 2.2 `update` semantics, stated precisely

`unchanged` / `upstream-changed` (catalog moved, disk clean) / `local-modified` (disk moved,
catalog clean) / `both` (conflict) / `removed-upstream`. `--apply` takes the first two, refuses
`both`, and reports what it skipped. `--force` takes `both`.

**Not** in scope: discovering that a *newer ref exists* (listing remote tags and semver-comparing).
That is "check for a new template release", a different feature. The UI's Refresh re-reads the
pinned ref; bumping the ref stays a manifest edit with a diff.

## 3. Information architecture

Full-page tab, five views. Selection is **per repository** — the unit `.ai/ai.json` already models.

### Browse — "what exists"
- Toolbar: search (id/description), filters `kind: skill|agent`, `source: <id>`, `state: installed|not installed|has update|modified here`, sort by name/kind/source.
- List: dense rows — icon, id, kind pill, source pill, description (one line, truncated), state dot.
- Detail pane: full description, install path, source + ref + license + upstream link, file count, hash (short), actions **Install** / **Remove** / **Copy id**.
- Bulk: "Install all N matching" in the toolbar (respects the active filter) — this is the
  "install everything" path for a filtered subset.
- Empty state: the *new repo* case — "No selection yet" with a **Set up this repo** primary action →
  Presets.

### Installed — "what this repo has"
- Header: `12 items · in sync` or `3 need attention`, materialize mode (link/copy) segmented control, **Sync** primary.
- Rows grouped by state: Needs attention (missing / modified here / conflict / gone upstream) then
  In sync. Row actions: open, re-sync one, remove.
- Deselected-but-on-disk items (lock-recorded but no longer in the manifest) get their own group with
  a **Prune** action and an explicit "deletes files" confirmation — never silent.

### Updates — "what changed upstream"
- Only meaningful when a source is `resolve: remote`; otherwise an explanatory empty state naming
  the reason and the one-line manifest change that would enable it.
- Summary bar: `2 upstream · 1 conflict · 1 modified here`, buttons **Update all safe** / **Refresh**.
- Rows with per-item **Update** / **Keep mine**; conflicts marked and require an explicit choice.

### Presets — "install everything in a new repo"
- Cards: `minimal`, `dotnet-web`, `maui-mobile`, `blazor`, `full` — name, description, resolved
  counts (`14 skills · 6 agents`), and the delta against what is installed now (`+8 / −2`).
- Primary action per card: **Use this preset** (writes `preset`, clears explicit lists it supersedes,
  syncs). `full` carries an "everything the catalog offers" note with the real count.
- One-click **Install everything** button, which is `init --preset full` for the current repo.

### Settings — route, not a tab (matches the reference)
- `aiManager.pythonPath`.
- Sources table: id, repo, ref, resolve local/remote, license, allowHooks — read-only with "Open .ai/ai.json".
- Prune policy toggle (leave files / prune on remove) mapping to the CLI flags.
- Buttons: **Regenerate catalog index**, **Write NOTICE.md**, **Open catalog/INDEX.md**.

## 4. RPC surface (the whole contract)

```ts
getBootstrap(): { workspaceName, hasManifest, hasScript, pythonPath, sameDir, version }
getBrowse(req: { Query?, Kind?, Source?, State?, Sort? }): { Items: CatalogItem[] }
getInstalled(): { Items: InstalledItem[], Summary, Materialize, Locked }
getUpdates(req: { Refresh? }): { Items: UpdateItem[], Summary, RemoteEnabled }
getPresets(): { Presets: PresetCard[] }
applyPreset(req: { Name, Sync }): OperationResult
install(req: { Ids[] }): OperationResult
remove(req: { Ids[], Prune }): OperationResult
sync(req: { Materialize? }): OperationResult
updateItems(req: { Ids[] | All, Force }): OperationResult
pruneDeseleded(): OperationResult
writeNotice(): OperationResult
rebuildIndex(): OperationResult
openFile(req: { Path }): void
openUpstream(req: { Id }): void
```
Every method returns `Result<T>` (`{ ok: true, value }` | `{ ok: false, error }`) like the reference,
so a failed CLI call renders as an inline error, never an unhandled rejection. Each maps to exactly
one `catalog.py` invocation — the extension never re-implements resolution.

## 5. File layout

```
extensions/ai-manager/
  esbuild.js                         two bundles (extension + web)
  package.json                       contributes: full-page command, launcher view, badge, config
  src/common/{types.ts,nonce.ts,rpc/{types,result,rpc-host,rpc-client}.ts}
  src/host/{extension.ts,host-api.ts,panel.ts,launcher.ts}
  src/web/{main.ts,router.ts,registrations.ts,types.ts}
  src/web/styles/{base,shared}.css.ts
  src/web/components/{catalog-shell,browse-view,item-row,item-details,installed-view,
                      updates-view,presets-view,preset-card,settings-view,search-bar,
                      filter-bar,state-pill,empty-state,toast-host}.ts
  src/web/**/*.test.ts               jsdom component tests (Lit renders headlessly)
  tools/{index.html,dev-server.mjs,mock-api.js}
```

The existing `src/model.ts`, `src/catalogClient.ts`, `src/extension.ts` and `test/model.test.js`
from the current extension are folded into this layout; `model.ts`'s parsing stays as the web-side
fallback and moves to `src/common/` where both sides can use it.

## 6. Verification

| Layer | How |
|---|---|
| CLI additions | `pytest` in `.ai/tests/test_catalog.py` — `browse`/`presets`/`init`/`update` including the four update states, `--force`, and `resolve: remote` against a local git fixture repo (no network) |
| RPC + host | node unit tests over `rpc-client`/`rpc-host` with a fake `postMessage` (the reference does exactly this in `rpc.test.ts`) |
| Web components | jsdom + Lit render tests over pure view logic (filtering, grouping, delta maths) |
| **Visual** | `tools/dev-server.mjs` + `mock-api.js` serve the real `dist/web.js` against fixtures; I screenshot every view in the browser tool, light and dark, and iterate |
| Packaging | `vsce package`, assert the VSIX stays small |

The visual row is the meaningful difference from the last round: the UI gets *seen*, not just compiled.

## 7. Phases

| Phase | Content | Exit criterion |
|---|---|---|
| **1 — Backend** | `browse`, `presets`, `init --preset/--all`, `update --apply/--force`, `resolve: remote`, schemas, pytest | `pytest` green; a scratch project with a remote local-git source shows all four update states |
| **2 — Shell** | esbuild, RPC layer, panel + serializer + launcher + badge, `getBootstrap` | Panel opens in a browser via the dev harness with a stub shell |
| **3 — Browse + Installed** | list/detail split, search, filters, install/remove/sync, state pills | Screenshots of both views, empty and populated |
| **4 — Presets + Updates** | preset cards with deltas, install-everything, updates view with the four states | Screenshot of the new-repo flow: empty → preset → installed |
| **5 — Settings + polish** | settings route, toasts, error surfaces, keyboard, packaging | `.vsix` built, all screenshots regenerated |

Each phase is independently reviewable and leaves the repo working.

## 8. Risks

| Risk | Mitigation |
|---|---|
| `resolve: remote` changes what `sync` means for existing projects | Default stays `local`; only manifests that opt in change. Asserted by a test that a `local` manifest behaves exactly as today |
| Fetch on every UI paint makes it slow | Fetch is cached per (source, ref) in `.ai/tmp/catalog-cache/`; `getBrowse` reads the cache and never fetches; only explicit Refresh and `update` may fetch |
| Web UI drifts from the CLI | The web layer holds no resolution logic: every list it renders comes from one CLI payload |
| A big UI landing in the template repo | `extensions/` stays out of `TEMPLATE_OWNED`, as today; the dev harness and screenshots live beside it |
| Scope creep into "newer release available" checks | Explicitly out of scope in §2.2 |

## 9. Decisions needed

1. **Copy the reference stack exactly** (Lit + esbuild + typed RPC + full-page tab + dev harness), or
   keep it lighter (one hand-rolled webview, no RPC layer)? Recommendation: copy it — the reference is
   proven, and the dev harness is what makes the UI verifiable.
2. **`resolve: remote` for real Updates**, or keep Updates limited to local-vs-lock drift (no network,
   weaker, and empty for today's projects)? Recommendation: add it, opt-in, default `local`.
3. **Bootstrap story for a new repo**: `init --preset full` writes `.ai/ai.json` and syncs the whole
   catalog (≈89 items today, ~530 once awesome-copilot is wired) — is "install everything" the
   intended default for a new repo, or should the UI nudge toward a preset and treat `full` as the
   explicit escape hatch? Recommendation: default to `dotnet-web`-style presets, `full` one click away
   and labelled with its real size.
4. **Where the repo-level selection lives in a multi-root workspace**: one `.ai/ai.json` per folder
   (natural, matches the resolver), with the panel following the active folder. Recommendation: adopt.
