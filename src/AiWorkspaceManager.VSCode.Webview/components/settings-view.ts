import { LitElement, css, html } from "lit";
import type { ClientName } from "../../AiWorkspaceManager.VSCode.Common/types";
import { agentFileDetails, agentStatusClass, releaseSummary, sourceRefLabel } from "../logic";
import { hostApi } from "../registrations";
import { store, type RunReport } from "../store";
import { sharedStyles } from "../styles/shared.css";

/** What each client gets on disk. Prompts are mentioned only as an option, because they are linked only when installed. */
const CLIENT_OPTIONS: { id: ClientName; label: string; detail: string }[] = [
  {
    id: "claude",
    label: "Claude Code",
    detail: ".claude/skills, agents, rules generated from .ai/instructions, and settings.json hooks (prompts only if installed)",
  },
  {
    id: "copilot",
    label: "GitHub Copilot",
    detail: ".github/skills, agents, instructions, copilot-instructions.md and .vscode workspace settings (prompts only if installed)",
  },
  { id: "agents", label: "Other agents", detail: ".agents/skills — Codex and other skills-standard agents" },
];

/** Settings: the knobs that are not per-item, plus the maintenance actions. */
export class SettingsView extends LitElement {
  private readonly onChange = (): void => this.requestUpdate();

  override connectedCallback(): void {
    super.connectedCallback();
    store.addEventListener("changed", this.onChange);
    // One `git ls-remote`, cached for the session; it never re-resolves a source.
    void store.ensureReleasesAsync();
    void store.ensureProjectAsync();
  }

  override disconnectedCallback(): void {
    store.removeEventListener("changed", this.onChange);
    super.disconnectedCallback();
  }

  static override readonly styles = [
    sharedStyles,
    css`
      :host {
        display: block;
        flex: 1;
        overflow: auto;
        padding: 16px 18px;
      }
      section {
        max-width: 720px;
        margin-bottom: 24px;
      }
      h2 {
        font-size: 13px;
        margin: 0 0 8px;
      }
      p {
        color: var(--vscode-descriptionForeground);
        line-height: 1.55;
        margin: 0 0 10px;
        font-size: 12px;
      }
      .row {
        display: flex;
        gap: 8px;
        align-items: center;
        margin-bottom: 8px;
      }
      .row input {
        flex: 1;
      }
      .clients {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .client {
        display: flex;
        gap: 10px;
        align-items: flex-start;
        font-size: 12px;
      }
      .client input {
        margin-top: 2px;
        accent-color: var(--vscode-focusBorder);
      }
      .client .detail {
        color: var(--vscode-descriptionForeground);
        font-size: 11.5px;
      }
      .row textarea {
        flex: 1;
        font: inherit;
        font-size: 12px;
        background: var(--vscode-input-background);
        color: var(--vscode-input-foreground);
        border: 1px solid var(--vscode-input-border, transparent);
        border-radius: 2px;
        padding: 5px 7px;
        resize: vertical;
      }
      .row textarea:focus {
        outline: 1px solid var(--vscode-focusBorder);
        outline-offset: -1px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 12px;
      }
      th,
      td {
        text-align: left;
        padding: 5px 8px;
        border-bottom: 1px solid color-mix(in srgb, var(--vscode-panel-border) 55%, transparent);
      }
      th {
        color: var(--vscode-descriptionForeground);
        font-weight: 600;
        font-size: 11px;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .actions {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .report td div {
        margin-bottom: 2px;
      }
      .report-lines {
        margin: 10px 0 0;
        max-height: 260px;
        overflow: auto;
        padding: 8px 10px;
        font-family: var(--vscode-editor-font-family, monospace);
        font-size: 11.5px;
        line-height: 1.5;
        white-space: pre;
        color: var(--vscode-foreground);
        background: var(--vscode-textCodeBlock-background, transparent);
        border: 1px solid color-mix(in srgb, var(--vscode-panel-border) 55%, transparent);
        border-radius: 3px;
      }
    `,
  ];

  private toggleClient(client: ClientName, enabled: boolean): void {
    const selected = new Set<ClientName>(store.browse?.clients ?? ["claude"]);
    if (enabled) {
      selected.add(client);
    } else {
      selected.delete(client);
    }
    void store.setClients(CLIENT_OPTIONS.map((option) => option.id).filter((id) => selected.has(id)));
  }

  /**
   * The last setup or sync: a row per merged client file (status, added, pinned, kept conflicts) and the
   * mirror report lines. Hidden until a run has happened.
   */
  private renderReport(report: RunReport) {
    return html`
      <section class="report">
        <h2>Report</h2>
        <p>${report.title} — the outcome of the last run in this session.</p>
        ${report.files.length > 0 ? html`
          <table>
            <thead><tr><th>File</th><th>Status</th><th>Changes</th></tr></thead>
            <tbody>
              ${report.files.map((file) => html`
                <tr>
                  <td class="mono">${file.relativePath}</td>
                  <td><span class="pill ${agentStatusClass(file.status)}">${file.status}</span></td>
                  <td>
                    ${agentFileDetails(file).length > 0
                      ? agentFileDetails(file).map((detail) => html`<div>${detail}</div>`)
                      : html`<span class="muted">nothing to change</span>`}
                  </td>
                </tr>
              `)}
            </tbody>
          </table>
        ` : ""}
        ${report.lines.length > 0
          ? html`<pre class="report-lines">${report.lines.join("\n")}</pre>`
          : ""}
      </section>
    `;
  }

  override render() {
    const bootstrap = store.bootstrap;
    const sources = store.browse?.sources ?? [];
    const releases = new Map((store.releases?.sources ?? []).map((row) => [row.id, row]));
    const clients = new Set<ClientName>(store.browse?.clients ?? ["claude"]);
    return html`
      <section>
        <h2>Resolver</h2>
        <p>
          Catalog resolution runs in the bundled .NET core, started by the extension. No Python
          interpreter or workspace copy of <code>catalog.py</code> is required.
          ${bootstrap?.HasScript === false
            ? html`<strong>The core binary is missing from this build; run <code>npm run build:core</code>.</strong>`
            : ""}
        </p>
      </section>

      <section>
        <h2>Project</h2>
        <p>
          The name is prefilled from the solution or the folder. Saving it fills
          <code>.ai/session-context.md</code>, the architecture/domain documents and every
          <code>{ApplicationName}</code> in the scaffold. Only documents that still carry the
          template markers are rewritten.
        </p>
        <div class="row">
          <input type="text" placeholder="Project name" .value=${store.projectName}
            @input=${(event: Event) => {
              store.projectName = (event.target as HTMLInputElement).value;
            }}
          />
        </div>
        <div class="row">
          <textarea rows="3" placeholder="What is this project for?" .value=${store.projectIntention}
            @input=${(event: Event) => {
              store.projectIntention = (event.target as HTMLTextAreaElement).value;
            }}
          ></textarea>
        </div>
        <div class="actions">
          <button class="secondary" ?disabled=${store.loading}
            @click=${() => void store.saveProjectAsync()}>Save project details</button>
          ${store.project
            ? html`<span class="muted mono">detected: ${store.project.detectedName}</span>`
            : ""}
        </div>
      </section>

      <section>
        <h2>Clients</h2>
        <p>
          Each client gets platform folders linked to the same <code>.ai/</code> content.
          Disabling a client removes only its links and its generated rules or Copilot instructions;
          real directories and <code>.github/workflows</code> are never touched. A missing
          <code>clients</code> field keeps every client for older projects. Hook items are linked
          per file into <code>.github/hooks/</code> when Copilot is enabled.
        </p>
        <div class="clients">
          ${CLIENT_OPTIONS.map((option) => html`
            <label class="client">
              <input
                type="checkbox"
                .checked=${clients.has(option.id)}
                ?disabled=${store.loading}
                @change=${(event: Event) =>
                  this.toggleClient(option.id, (event.target as HTMLInputElement).checked)}
              />
              <span>
                <strong>${option.label}</strong><br />
                <span class="detail">${option.detail}</span>
              </span>
            </label>
          `)}
        </div>
      </section>

      ${store.report ? this.renderReport(store.report) : ""}

      <section>
        <h2>Sources</h2>
        <p>
          A <strong>local</strong> source reads this repository's own tree: Repository and Ref are
          provenance, not a fetch pin. A <strong>remote</strong> source pins a ref and fetches it,
          which is what gives the lock something to compare and makes the Updates view real. The
          Release column always asks the Repository for its tags, so even a local source shows what
          upstream publishes.
        </p>
        <p>
          <strong>Hooks</strong> are shell scripts, so they are indexed but never installed until a
          source opts in with <code>"allowHooks": true</code> in <code>.ai/ai.json</code>.
        </p>
        <table>
          <thead>
            <tr><th>Id</th><th>Repository</th><th>Ref</th><th>Release</th><th>Resolve</th><th>License</th><th>Hooks</th></tr>
          </thead>
          <tbody>
            ${sources.map((source) => html`
              <tr>
                <td class="mono">${source.id}</td>
                <td class="mono">${source.repo}</td>
                <td class="mono" title=${source.ref === "local"
                  ? "Not pinned — reads this repository's own tree"
                  : "Pinned revision"}>
                  ${sourceRefLabel(source.ref)}
                </td>
                <td class="mono" title=${releases.get(source.id)?.repo
                  ? `Tags published by ${releases.get(source.id)?.repo}`
                  : ""}>
                  ${releaseSummary(releases.get(source.id))}
                </td>
                <td>${source.resolve}</td>
                <td>${source.license}</td>
                <td title=${source.allowHooks
                  ? "Hooks from this source may be installed"
                  : "Hooks are shell scripts. Installing one needs \"allowHooks\": true on this source in .ai/ai.json."}>
                  ${source.allowHooks ? "allowed" : "opt-in"}
                </td>
              </tr>
            `)}
          </tbody>
        </table>
        <div class="actions" style="margin-top: 8px;">
          <button class="ghost" ?disabled=${store.loading}
            @click=${() => void store.refreshReleasesAsync()}>Check for newer releases</button>
          ${store.releases?.offline
            ? html`<span class="muted">Offline — releases were not checked.</span>`
            : ""}
        </div>
      </section>

      <section>
        <h2>Maintenance</h2>
        <div class="actions">
          <button class="secondary" ?disabled=${store.loading}
            title="Creates .ai/ai.json, the resolver/vocabulary under .ai/, and — on a new project — the template folders (plans, patterns, reference, …)"
            @click=${() => void store.bootstrapAsync()}>Set up this repository from the template</button>
          <button class="secondary" ?disabled=${store.loading}
            @click=${() => void store.sync()}>Sync selection</button>
          <button class="ghost" ?disabled=${store.loading}
            title="Copies any missing template .ai/ folders and files (patterns, reference, tests, plans, …). Existing files are never changed or removed."
            @click=${() => void store.addScaffold()}>Add template folders</button>
          <button class="ghost" ?disabled=${store.loading}
            title="Writes a local index to .ai/tmp (gitignored); the committed vocabulary stays under .ai/catalog"
            @click=${() => void store.rebuildIndex()}>Rebuild catalog index</button>
          <button class="ghost" ?disabled=${store.loading}
            @click=${() => void store.writeNotice()}>Write NOTICE.md</button>
          <button class="ghost" @click=${() => void hostApi.openFile({ Path: ".ai/ai.json" })}>
            Open .ai/ai.json
          </button>
        </div>
      </section>

      <section>
        <h2>About</h2>
        <p>
          AI Workspace Manager ${bootstrap?.Version ?? ""} — a front-end for the resolver in
          the bundled .NET core. Every list you see comes from one sidecar request;
          this view holds no resolution logic of its own.
        </p>
      </section>
    `;
  }
}
customElements.define("settings-view", SettingsView);
