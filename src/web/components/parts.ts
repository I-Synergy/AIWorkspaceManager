import { LitElement, css, html } from "lit";
import { statusClass, statusLabel } from "../logic";
import { sharedStyles } from "../styles/shared.css";

export class StatePill extends LitElement {
  static override properties = {
    status: { type: String },
    selected: { type: Boolean },
  };

  status = "available";
  selected = true;

  static override styles = [sharedStyles];

  override render() {
    return html`<span class="pill ${statusClass(this.status)}">${statusLabel(this.status, this.selected)}</span>`;
  }
}
customElements.define("state-pill", StatePill);

export class EmptyState extends LitElement {
  static override properties = {
    heading: { type: String },
    hint: { type: String },
  };

  heading = "";
  hint = "";

  static override styles = [
    sharedStyles,
    css`
      :host {
        display: flex;
        flex: 1;
        align-items: center;
        justify-content: center;
        padding: 40px 24px;
        text-align: center;
      }
      .card {
        max-width: 460px;
      }
      h2 {
        font-size: 15px;
        font-weight: 600;
        margin: 0 0 8px;
      }
      p {
        margin: 0 0 16px;
        color: var(--vscode-descriptionForeground);
        line-height: 1.5;
      }
    `,
  ];

  override render() {
    return html`
      <div class="card">
        <h2>${this.heading}</h2>
        <p>${this.hint}</p>
        <slot></slot>
      </div>
    `;
  }
}
customElements.define("empty-state", EmptyState);

/** Inline glyphs so no icon font dependency is needed. */
export function kindIcon(kind: string) {
  const path = kind === "agent"
    ? "M8 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm0 1.5c-2.7 0-5 1.6-5 3.5V15h10v-2c0-1.9-2.3-3.5-5-3.5Z"
    : kind === "prompt"
      ? "M3 2.5h10v8H8l-3 2.5V10.5H3v-8Zm2 2v1h6v-1H5Zm0 3v1h4V7.5H5Z"
      : "M3 2.5A1.5 1.5 0 0 1 4.5 1h7A1.5 1.5 0 0 1 13 2.5v11l-5-2.2L3 13.5V2.5Z";
  return html`<svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true">
    <path d=${path} fill="currentColor" />
  </svg>`;
}
