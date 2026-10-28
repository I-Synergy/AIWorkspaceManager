import { LitElement, css, html } from "lit";
import { releaseSummary, sourceRefLabel } from "../logic";
import { hostApi } from "../registrations";
import { store } from "../store";
import { sharedStyles } from "../styles/shared.css";

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

  static override styles = [
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
    `,
  ];

  override render() {
    const bootstrap = store.bootstrap;
    const sources = store.browse?.sources ?? [];
    const releases = new Map((store.releases?.sources ?? []).map((row) => [row.id, row]));
    return html`
      <section>
        <h2>Interpreter</h2>
        <p>The resolver runs as <code>python .ai/scripts/catalog.py …</code> from the workspace root.</p>
        <div class="row">
          <input type="text" .value=${bootstrap?.PythonPath ?? "python"}
            @change=${(event: Event) =>
              void hostApi.setPythonPath({ Value: (event.target as HTMLInputElement).value })}
            @keydown=${(event: KeyboardEvent) => {
              if (event.key === "Enter") {
                void hostApi.setPythonPath({ Value: (event.target as HTMLInputElement).value });
              }
            }}
          />
        </div>
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
            title="Creates .ai/ai.json, the resolver/vocabulary under .ai/, and any missing template folders (plans, patterns, reference, …)"
            @click=${() => void store.bootstrapAsync()}>Set up this repository from the template</button>
          <button class="secondary" ?disabled=${store.loading}
            @click=${() => void store.sync()}>Sync selection</button>
          <button class="ghost" ?disabled=${store.loading}
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
          AI Manager ${bootstrap?.Version ?? ""} — a front-end for the resolver in
          <code>.ai/scripts/catalog.py</code>. Every list you see comes from one CLI payload;
          this view holds no resolution logic of its own.
        </p>
      </section>
    `;
  }
}
customElements.define("settings-view", SettingsView);
