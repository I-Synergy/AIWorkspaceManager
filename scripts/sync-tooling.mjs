/**
 * Build-time: make sure the resolver's tooling is present in this extension's
 * `tooling/` folder, so the VSIX can install it into a repository that does not
 * have it.
 *
 * Two modes, chosen by what is on disk:
 *
 *   1. A template checkout is available (`AI_TEMPLATE_ROOT`, or the legacy
 *      `../../.ai/scripts/catalog.py` when the extension lives inside the
 *      template repository). Its files are copied in, so the packaged copy
 *      always matches the scripts that shipped with that commit.
 *   2. No checkout: `tooling/` is the committed snapshot and is used as-is,
 *      after verifying every manifest entry is present. This is what makes the
 *      standalone repository buildable on a fresh clone.
 *
 * `npm run sync-tooling -- --check` verifies the snapshot without writing, which
 * is what CI runs.
 */
import { cp, mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const EXTENSION = join(HERE, "..");
const OUT = join(EXTENSION, "tooling");

const FILES = [
  // The resolver and the scripts it drives.
  [".ai/scripts/catalog.py", ".ai/scripts/catalog.py"],
  [".ai/scripts/sync-platforms.py", ".ai/scripts/sync-platforms.py"],
  [".ai/scripts/sync-skills.py", ".ai/scripts/sync-skills.py"],
  [".ai/scripts/sync-agents.py", ".ai/scripts/sync-agents.py"],
  [".ai/scripts/upgrade-template.py", ".ai/scripts/upgrade-template.py"],
  // Template maintenance scripts: a project that installs from the template
  // should have the whole `.ai/scripts/` set, not only the resolver's helpers.
  [".ai/scripts/hygiene-lint.py", ".ai/scripts/hygiene-lint.py"],
  [".ai/scripts/migrate-to-ai.py", ".ai/scripts/migrate-to-ai.py"],
  // A template-only check, shipped so the extension can correct a project
  // whose pinned template predates the fix that makes it skip derived repos.
  [".ai/tests/validate-release.py", ".ai/tests/validate-release.py"],
  // Vocabulary, presets and schemas: without these a fresh repo has no
  // categories and no presets to choose from.
  ["catalog/presets.json", "catalog/presets.json"],
  ["catalog/categories.json", "catalog/categories.json"],
  ["catalog/schemas/ai.schema.json", "catalog/schemas/ai.schema.json"],
  ["catalog/schemas/index.schema.json", "catalog/schemas/index.schema.json"],
  ["catalog/schemas/lock.schema.json", "catalog/schemas/lock.schema.json"],
];

async function exists(path) {
  return stat(path).then(() => true, () => false);
}

/** Where the template lives, if a checkout is available at all. */
async function templateRoot() {
  const candidates = [];
  const override = process.env.AI_TEMPLATE_ROOT;
  if (override) {
    candidates.push(override);
  }
  // The layout the extension had while it lived inside the template repo.
  candidates.push(join(EXTENSION, "..", ".."));

  // The resolver may live only in this bundle (the template can drop it once
  // the extension ships it), so any resolver sibling identifies a checkout.
  const checks = await Promise.all(candidates.map(async (candidate) => ({
    candidate,
    found: await exists(join(candidate, ".ai", "scripts", "catalog.py"))
      || await exists(join(candidate, ".ai", "scripts", "sync-platforms.py"))
      || await exists(join(candidate, ".ai", "scripts", "sync-skills.py")),
  })));
  return checks.find((check) => check.found)?.candidate;
}

/** Fail before touching anything: a half-copied tree is worse than none. */
async function copyFrom(root) {
  const staging = join(EXTENSION, ".tooling-staging");
  await rm(staging, { recursive: true, force: true });
  await mkdir(staging, { recursive: true });

  const results = await Promise.all(FILES.map(async ([from, to]) => {
    const destination = join(staging, to);
    await mkdir(dirname(destination), { recursive: true });
    const templatePath = join(root, from);
    let fromSnapshot = false;
    if (await exists(templatePath)) {
      await cp(templatePath, destination);
    } else if (await exists(join(OUT, ...to.split("/")))) {
      // The template no longer ships this file (the resolver moved into the
      // extension, for example). Keep the committed snapshot rather than
      // producing an incomplete bundle.
      await cp(join(OUT, ...to.split("/")), destination);
      fromSnapshot = true;
    } else {
      return { absent: from };
    }
    const content = await readFile(destination);
    return { entry: { path: to, bytes: content.length }, kept: fromSnapshot ? to : null };
  }));
  const absent = results.filter((result) => result.absent).map((result) => result.absent);
  const kept = results.filter((result) => result.kept).map((result) => result.kept);
  const manifest = results.filter((result) => result.entry).map((result) => result.entry);
  if (absent.length > 0) {
    await rm(staging, { recursive: true, force: true });
    throw new Error(`no copy of ${absent.join(", ")} in the template or in tooling/`);
  }

  await writeFile(join(staging, "tooling.json"),
    JSON.stringify({ files: manifest }, null, 2) + String.fromCodePoint(10));

  // Swap only once the new snapshot is complete.
  await rm(OUT, { recursive: true, force: true });
  await cp(staging, OUT, { recursive: true });
  await rm(staging, { recursive: true, force: true });
  if (kept.length > 0) {
    console.log(`tooling: kept ${kept.length} file(s) the template no longer ships: `
      + kept.join(", "));
  }
  console.log(`tooling: ${manifest.length} file(s) collected from ${root}`);
}

/** Verify the committed snapshot: every expected file present, sizes as recorded. */
async function verifySnapshot() {
  const manifestPath = join(OUT, "tooling.json");
  if (!(await exists(manifestPath))) {
    throw new Error("tooling/ is missing — run `npm run sync-tooling` with "
      + "AI_TEMPLATE_ROOT pointing at a template checkout");
  }
  const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
  const recorded = new Map((manifest.files || []).map((entry) => [entry.path, entry.bytes]));

  const problems = (await Promise.all(FILES.map(async ([, to]) => {
    if (!recorded.has(to)) {
      return `${to} is not in tooling.json`;
    }
    const path = join(OUT, ...to.split("/"));
    if (!(await exists(path))) {
      return `${to} is missing`;
    }
    const size = (await stat(path)).size;
    return size === recorded.get(to)
      ? null
      : `${to} is ${size} bytes, tooling.json says ${recorded.get(to)}`;
  }))).filter(Boolean);
  if (problems.length > 0) {
    throw new Error(`the committed tooling snapshot is inconsistent:\n  - ${problems.join("\n  - ")}`);
  }
  console.log(`tooling: committed snapshot verified (${recorded.size} file(s))`);
}

/** Recompute tooling.json from the files in `tooling/` (used after editing the
 *  committed snapshot by hand, which is how a standalone checkout updates it). */
async function stampSnapshot() {
  const missing = (await Promise.all(FILES.map(async ([, to]) =>
    (await exists(join(OUT, ...to.split("/")))) ? null : to))).filter(Boolean);
  if (missing.length > 0) {
    throw new Error(`cannot stamp: ${missing.join(", ")} missing from tooling/`);
  }
  const manifest = await Promise.all(FILES.map(async ([, to]) => {
    const path = join(OUT, ...to.split("/"));
    return { path: to, bytes: (await stat(path)).size };
  }));
  await writeFile(join(OUT, "tooling.json"),
    JSON.stringify({ files: manifest }, null, 2) + "\n");
  console.log(`tooling: stamped tooling.json (${manifest.length} file(s))`);
}

async function main() {
  if (process.argv.includes("--check")) {
    await verifySnapshot();
    return;
  }
  if (process.argv.includes("--stamp")) {
    await stampSnapshot();
    return;
  }

  const root = await templateRoot();
  if (root) {
    await copyFrom(root);
    return;
  }

  // Standalone checkout: the snapshot is the source of truth. Verify rather
  // than silently packaging an incomplete resolver.
  await verifySnapshot();
  console.log("tooling: no template checkout found; using the committed snapshot "
    + "(set AI_TEMPLATE_ROOT to refresh it)");
}

try {
  await main();
} catch (error) {
  console.error(`tooling: ${error.message}`);
  process.exitCode = 1;
}
