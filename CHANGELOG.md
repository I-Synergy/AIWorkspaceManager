# Changelog

## Unreleased

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
