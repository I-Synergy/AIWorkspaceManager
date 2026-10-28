import { execFile } from "node:child_process";
import * as path from "node:path";
import type { CliResult } from "../common/types";

export function manifestPath(workspaceRoot: string): string {
  return path.join(workspaceRoot, ".ai", "ai.json");
}

/**
 * The resolver the extension runs.
 *
 * It ships inside the VSIX (`tooling/scripts/catalog.py`) and is pointed at the
 * workspace with `CATALOG_ROOT`, so **a repository needs no copy of these
 * scripts to use AI Workspace Manager**. Projects that want the CLI themselves — CI, agents
 * in a terminal, a clone without the extension — can vendor it with
 * `aiManager.vendorTooling`; that is a convenience, not a prerequisite.
 */
export function scriptPath(extensionPath: string): string {
  return path.join(extensionPath, "tooling", ".ai", "scripts", "catalog.py");
}

/**
 * Run the resolver against a workspace. Resolves for any exit code: `sync
 * --check`, `update` and `notice --verify` all use a non-zero code to mean "here
 * is information", and the caller decides whether that is an error.
 */
export function runCatalog(
  pythonPath: string,
  script: string,
  workspaceRoot: string,
  args: string[],
): Promise<CliResult> {
  return new Promise((resolve, reject) => {
    execFile(
      pythonPath,
      [script, ...args],
      {
        cwd: workspaceRoot,
        // CATALOG_TOOLING lets the resolver fill files the template no longer
        // ships (the resolver itself) from this bundle.
        env: {
          ...process.env,
          CATALOG_ROOT: workspaceRoot,
          CATALOG_TOOLING: path.resolve(path.dirname(script), "..", ".."),
        },
        maxBuffer: 64 * 1024 * 1024,
        windowsHide: true,
      },
      (error, stdout, stderr) => {
        if (error && typeof error.code === "string") {
          reject(new Error(`could not run ${pythonPath}: ${error.message}`));
          return;
        }
        resolve({ code: error && typeof error.code === "number" ? error.code : 0, stdout, stderr });
      },
    );
  });
}

/** Parse a CLI payload, turning Python's stderr into a readable failure. */
export function parseCliJson<T>(result: CliResult, what: string): T {
  const text = result.stdout.trim();
  if (!text) {
    const detail = result.stderr.trim().split(/\r?\n/)[0] ?? "no output";
    throw new Error(`${what} failed (exit ${result.code}): ${detail}`);
  }
  try {
    return JSON.parse(text) as T;
  } catch {
    const head = text.split(/\r?\n/, 2).join(" ");
    throw new Error(`${what} did not return JSON: ${head.slice(0, 200)}`);
  }
}
