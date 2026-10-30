import * as vscode from "vscode";
import { execFile } from "node:child_process";
import * as nodePath from "node:path";
import { manifestPath, parseCliJson, runCatalog, scriptPath } from "./cli";
import type {
  AdoptPayload, BootstrapPayload, Bootstrap, BrowsePayload, CliResult, HostAPI, PresetsPayload,
  ProjectInfo, ReleasePayload, Result, UpdatePayload, UpgradeResult,
} from "../common/types";
import { fail, ok } from "../common/types";

export interface HostApiContext {
  workspaceRoot: () => string | undefined;
  pythonPath: () => string;
  extensionVersion: string;
  /** Where the VSIX lives; the bundled resolver is read from here. */
  extensionPath: string;
  /** Resolves once the activation-time setup has finished. Calls that need the
      repository's files wait for it; `bootstrap` itself does not. */
  ready?: Promise<void>;
}

async function guard<T>(work: () => Promise<T>): Promise<Result<T>> {
  try {
    return ok(await work());
  } catch (error) {
    return fail(error instanceof Error ? error.message : String(error));
  }
}

/**
 * Every method maps to exactly one `catalog.py` invocation. No resolution logic
 * lives here — if the CLI cannot answer a question, the UI cannot answer it
 * either, and that is deliberate.
 */
export function createHostAPI(context: HostApiContext): HostAPI {
  const cli = async (args: string[], wait = true): Promise<CliResult> => {
    if (wait && context.ready) {
      await context.ready;
    }
    const root = context.workspaceRoot();
    if (!root) {
      throw new Error("Open a folder before using AI Workspace Manager.");
    }
    // The resolver ships with the extension: nothing of it has to exist in the
    // repository being managed.
    return runCatalog(context.pythonPath(), scriptPath(context.extensionPath), root, args);
  };

  const json = async <T>(args: string[], what: string, wait = true): Promise<T> =>
    parseCliJson<T>(await cli(args, wait), what);

  const exists = (uri: vscode.Uri): Promise<boolean> =>
    Promise.resolve(vscode.workspace.fs.stat(uri).then(() => true, () => false));

  return {
    async getBootstrap() {
      return guard(async () => {
        if (context.ready) {
          await context.ready;
        }
        const root = context.workspaceRoot();
        // Always true now that the resolver is bundled — kept in the contract so
        // the UI can still report a genuinely broken install.
        const hasScript = await exists(vscode.Uri.file(scriptPath(context.extensionPath)));
        const hasManifest = root ? await exists(vscode.Uri.file(manifestPath(root))) : false;
        let materialize: "link" | "copy" = "link";
        if (hasManifest) {
          try {
            const raw = await vscode.workspace.fs.readFile(vscode.Uri.file(manifestPath(root!)));
            const parsed = JSON.parse(Buffer.from(raw).toString("utf8")) as { materialize?: string };
            materialize = parsed.materialize === "copy" ? "copy" : "link";
          } catch {
            /* a malformed manifest is reported by browse, not here */
          }
        }
        const bootstrap: Bootstrap = {
          WorkspaceName: vscode.workspace.workspaceFolders?.[0]?.name ?? "(no folder)",
          HasManifest: hasManifest,
          HasScript: hasScript,
          PythonPath: context.pythonPath(),
          Materialize: materialize,
          Version: context.extensionVersion,
        };
        return bootstrap;
      });
    },

    async getBrowse(params) {
      return guard(() => json<BrowsePayload>(
        ["browse", "--json", ...(params?.Refresh ? ["--refresh"] : [])], "catalog.py browse"));
    },

    async getUpdates() {
      // `update` refreshes remote sources itself; it has no --refresh flag.
      return guard(() => json<UpdatePayload>(["update", "--json"], "catalog.py update"));
    },

    async getPresets() {
      return guard(() => json<PresetsPayload>(["presets", "--json"], "catalog.py presets"));
    },

    async getReleases() {
      return guard(() => json<ReleasePayload>(
        ["release", "--json"], "catalog.py release"));
    },

    async adopt() {
      return guard(() => json<AdoptPayload>(["adopt", "--json"], "catalog.py adopt"));
    },

    async bootstrap() {
      // Does not wait for `ready`: it *is* what activation waits for.
      return guard(() => json<BootstrapPayload>(
        ["bootstrap", "--json"], "catalog.py bootstrap", false));
    },

    async getProject() {
      return guard(() => json<ProjectInfo>(["project", "--json"], "catalog.py project"));
    },

    async describeProject(params) {
      return guard(() => json<ProjectInfo>(
        ["project", "--name", params.Name, "--intention", params.Intention, "--json"],
        "catalog.py project"));
    },

    async upgrade(params) {
      return guard(async (): Promise<UpgradeResult> => {
        const choice = await vscode.window.showWarningMessage(
          `Pin source "${params.Id}" to ${params.Release} and replace the installed `
            + "files and template scripts with that release? Local edits to those files are lost.",
          { modal: true },
          "Update",
        );
        if (choice !== "Update") {
          return { Cancelled: true, Code: 0, Stdout: "", Stderr: "" };
        }
        const pin = await cli(["pin", "--release", params.Release, "--json"]);
        if (pin.code !== 0) {
          return { Cancelled: false, Code: pin.code, Stdout: pin.stdout, Stderr: pin.stderr };
        }
        const update = await cli(["update", "--apply", "--tooling", "--json"]);
        return {
          Cancelled: false,
          Code: update.code,
          Stdout: update.stdout,
          Stderr: update.stderr,
        };
      });
    },

    async addPreset(params) {
      return guard(async () => {
        // Additive: `init --preset` would replace the whole selection, which
        // made applying a second preset deselect the first one's items.
        await cli(["preset", "add", params.Name]);
        return cli(["sync"]);
      });
    },

    async setLocked(params) {
      return guard(() => cli([params.Locked ? "lock" : "unlock", ...params.Ids]));
    },

    async install(params) {
      return guard(async () => {
        await cli(["add", ...params.Ids]);
        return cli(["sync"]);
      });
    },

    async remove(params) {
      return guard(async () => {
        await cli(["remove", ...params.Ids]);
        return cli(params.Prune ? ["sync", "--prune"] : ["sync"]);
      });
    },

    async sync() {
      return guard(() => cli(["sync"]));
    },

    async updateItems(params) {
      return guard(() =>
        cli(["update", "--apply", ...(params?.Ids ?? []), ...(params?.Force ? ["--force"] : [])]));
    },

    async pruneDeselected() {
      return guard(() => cli(["sync", "--prune"]));
    },

    async writeNotice() {
      return guard(() => cli(["notice"]));
    },

    async rebuildIndex() {
      return guard(() => cli(["index", "--compact"]));
    },

    async addScaffold() {
      return guard(() => cli(["scaffold", "--json"]));
    },

    async setClients(params) {
      return guard(async () => {
        await cli(["clients", "--set", ...params.Clients]);
        // Reconcile junctions, Copilot instructions and client folders.
        return cli(["sync"]);
      });
    },

    async openFile(params) {
      return guard(async () => {
        const root = context.workspaceRoot();
        if (!root) {
          throw new Error("Open a folder first.");
        }
        const document = await vscode.workspace.openTextDocument(
          vscode.Uri.file(`${root}/${params.Path}`));
        await vscode.window.showTextDocument(document, { preview: true });
        return null;
      });
    },

    async openUpstream(params) {
      return guard(async () => {
        await vscode.env.openExternal(vscode.Uri.parse(params.Url));
        return null;
      });
    },

    async vendorTooling(params) {
      return guard(async () => {
        const root = context.workspaceRoot();
        if (!root) {
          throw new Error("Open a folder first.");
        }
        const installer = nodePath.join(context.extensionPath, "scripts", "install-tooling.mjs");
        const args = [installer, root];
        if (params?.Overwrite) {
          args.push("--overwrite");
        }
        const output = await new Promise<string>((resolve, reject) => {
          execFile(process.execPath, args, { windowsHide: true }, (error, stdout, stderr) => {
            if (error) {
              reject(new Error((stderr || stdout || error.message).trim()));
              return;
            }
            resolve(stdout);
          });
        });
        const nl = String.fromCodePoint(10);
        const lastLine = output.trim().split(nl).pop() ?? "{}";
        const summary = JSON.parse(lastLine) as {
          copied?: string[]; skipped?: string[]; manifest?: string;
        };
        return {
          Copied: (summary.copied ?? []).length,
          Skipped: (summary.skipped ?? []).length,
          Manifest: summary.manifest ?? "kept",
        };
      });
    },

    async setPythonPath(params) {
      return guard(async () => {
        await vscode.workspace.getConfiguration("aiManager")
          .update("pythonPath", params.Value, vscode.ConfigurationTarget.Workspace);
        return null;
      });
    },
  };
}
