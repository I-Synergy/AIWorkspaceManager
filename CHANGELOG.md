# Changelog

## Unreleased

- Renamed to **AI Workspace Manager for VS Code** with the Marketplace name `aiworkspacemanager`; the repository moved to `https://github.com/I-Synergy/AIWorkspaceManager`.
- Refreshed the UI copy, screenshots, animated demo and social preview.

## 0.10.0 (2026-10-28)

- First Marketplace release of AI Workspace Manager for VS Code.
- Browse, Installed, Updates, Presets and Settings surfaces for the per-repository skill/agent selection.
- Newer-template-release detection against the source's git tags, with a one-click upgrade to the newest release.
- Self-contained resolver: the extension ships the `.ai/scripts` tooling and vocabulary, and sets a repository up on first activation.
- Project form (name/intention) fills `session-context.md`, the project documents and `{ApplicationName}` across the scaffold.
- Generated agent instructions: `AGENTS.md` is the source of truth and `.github/copilot-instructions.md` is derived from it, listing only what is installed.
