# Changelog

## Unreleased

- `microsoft/aspire-skills` is now a built-in source (`aspire-skills`) with its seven official Aspire skills and a new `Aspire` preset. It is indexed before `awesome-copilot` because both ship an `aspire` skill; the official one keeps the name.
- Existing projects pick up newly offered built-in sources automatically: `bootstrap` (run on activation) adds any missing source entry to `.ai/ai.json` without touching the selection. Built-in source priority now follows the resolver's declaration order, not the order a manifest happened to be written in.
- The `template` bucket is labelled **I-Synergy** instead of "Template".
- Presets: `.Net Maui` also selects the useful `dotnet-maui` reviewer agent, and `Devops` includes the Aspire deployment skill; the preset copy no longer says "both sources".
- Added **prompts** as a first-class kind next to instructions: prompts are read from `prompts/` (`*.prompt.md` or plain `*.md`), installed under `.ai/prompts/`, shown in the kind filter, and written to `.ai/ai.json` as `prompts: [...]`.
- Renamed the template's `sync-skills.py` to **`sync-platforms.py`**, since it now mirrors skills, agents, instructions and prompts. `sync-skills.py` remains as a deprecated alias, so existing hooks and project instructions keep working. The Claude `SessionStart`/`PostToolUse` hooks now also fire on `.ai/prompts/` and `.ai/instructions/` changes.
- `sync --prune` now preserves lock entries whose kind the running resolver does not know instead of treating them as deselected. A newer client can therefore add a kind (like `prompt`) without an older client's prune deleting it. Resolvers shipped before this change still have the old behaviour, so upgrade before pruning a project that has prompts.
- Renamed the `update-skills` skill to **`sync-platforms`**; it now documents all four junction sets (skills, agents, prompts, instructions) and the new script.
- Instructions and prompts now mirror to `.github/instructions/` and `.github/prompts/` alongside the skill/agent junctions. The AI template's own `.ai/instructions/` and `.ai/prompts/` are selectable without further configuration.
- Presets are ordered alphabetically (case-insensitive, leading dot ignored). **Tools** is 13th, **Minimal** 14th and **Everything** last. Added a **Minimal** preset with a small template baseline, and renamed the `full` preset to `Everything` (the old `full` value in an existing manifest still resolves through an alias).
- `dotnet/maui-labs` is now offered as two built-in third-party sources, `dotnet-maui` and `dotnet-maui-tooling`, instead of copied content. Each source reads its own plugin's skills; `dotnet-maui` also contributes the repository's agents and instructions. Sources can declare per-kind `paths`, which is how the nested `plugins/<plugin>/skills` layout is read (and an empty list disables a kind).
- The `.NET MAUI` and `Design` presets select those skills from the `dotnet-maui:` and `dotnet-maui-tooling:` namespaces; nothing is vendored into the template catalog.

## 0.11.0 (2026-10-28)

- Generated-file markers moved to the `ai-workspace-manager:` namespace (`generated-from-agents`, `copilot-reference:start`/`:end`, `installed:start`/`:end`). Files written with the old markers are still recognized and are rewritten with the new namespace on the next regeneration — no duplicated or lost reference block.
- README now documents where the catalog comes from: the `I-Synergy/AI` template and `github/awesome-copilot`, with pinned refs, licences and provenance in `.ai/ai.lock.json`.
- Marketplace categories now include **AI** and **Machine Learning**.

## 0.10.1 (2026-10-28)

- Renamed to **AI Workspace Manager for VS Code** with the Marketplace name `aiworkspacemanager`; the repository moved to `https://github.com/I-Synergy/AIWorkspaceManager`.
- Refreshed the UI copy, screenshots, animated demo and social preview.

## 0.10.0 (2026-10-28)

- First Marketplace release of AI Workspace Manager for VS Code.
- Browse, Installed, Updates, Presets and Settings surfaces for the per-repository skill/agent selection.
- Newer-template-release detection against the source's git tags, with a one-click upgrade to the newest release.
- Self-contained resolver: the extension ships the `.ai/scripts` tooling and vocabulary, and sets a repository up on first activation.
- Project form (name/intention) fills `session-context.md`, the project documents and `{ApplicationName}` across the scaffold.
- Generated agent instructions: `AGENTS.md` is the source of truth and `.github/copilot-instructions.md` is derived from it, listing only what is installed.
