🚀 AI Workspace Manager for VS Code — stop hand-copying skills and agents into every repo

The problem Copilot, Claude Code and Reasonix only follow the skills, agents and instructions present in the repo. Getting them there is manual, and it fails predictably:

• All-or-nothing — cloning the template brings the whole catalog, including what a project will never use. No per-repo selection, no dependency closure. 
• Silent drift — installed files get edited in place; the next template update overwrites that work or is skipped, and nobody can tell template-owned from locally changed or stale. 
• No provenance — where an item came from, which revision, its licence, or whether upstream moved is hard to answer. 
• Sources do not compose — the template catalog and awesome-copilot overlap, so duplicates and name collisions surface only after install. 
• Risky updates — without a lock and content hashes, updating is a re-clone that can delete local changes.

The solution A full-page manager for the skills and agents each repo installs, over one catalog built from I-Synergy/AI and github/awesome-copilot.

• Browse, search, filter and bulk-select; apply presets with dependencies resolved.
• Only what you selected lands in the repo — one selection in .ai/ai.json, reviewable in a diff. 
• Pinned refs plus per-item content hashes: updates are a diff, with local-modified / upstream-changed / conflict states. 
• Nothing destructive by default — removing edits the selection; files leave disk only via an explicit prune. 
• First activation sets up .ai/, AGENTS.md, Copilot/Claude config and the .claude / .github / .reasonix junctions.

Marketplace: https://marketplace.visualstudio.com/items?itemName=I-Synergy.aiworkspacemanager
Source:  https://github.com/I-Synergy/AIWorkspaceManager