#!/usr/bin/env node

// Release script for AI Workspace Manager for VS Code.
// Usage: node tools/release.mjs [patch|minor|major|X.Y.Z] [--dry-run]
//
// - Bumps version in package.json + package-lock.json
// - Updates CHANGELOG.md (moves Unreleased into a versioned section)
// - Extracts release notes to RELEASE_NOTES.md (used by CI for GitHub Releases)
// - Commits and pushes; CI publishes the Marketplace release and tags it

import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { parseArgs } from "node:util";
import { join } from "node:path";

const ROOT = join(import.meta.dirname, "..");

// -- Argument parsing --

const { values, positionals } = parseArgs({
  args: process.argv.slice(2),
  options: {
    "dry-run": { type: "boolean", default: false },
  },
  allowPositionals: true,
  strict: true,
});

const dryRun = values["dry-run"];
const bumpArg = positionals[0] || "patch";
const validBumps = new Set(["patch", "minor", "major"]);
const isExplicitVersion = !validBumps.has(bumpArg) && /^\d+\.\d+\.\d+$/.test(bumpArg);

if (!validBumps.has(bumpArg) && !isExplicitVersion) {
  console.error(`Invalid version argument: "${bumpArg}"`);
  console.error("Usage: node tools/release.mjs [patch|minor|major|X.Y.Z] [--dry-run]");
  process.exit(1);
}

// -- Helpers --

function run(cmd, args, opts = {}) {
  return execFileSync(cmd, args, {
    cwd: ROOT,
    encoding: "utf-8",
    ...opts,
  }).trim();
}

function git(...args) {
  return run("git", args);
}

// -- Pre-flight checks (skipped in dry-run mode) --

const branch = git("branch", "--show-current");
const releaseBranches = ["main", "development/main"];

if (!dryRun) {
  const dirtyFiles = git("status", "--porcelain");
  if (dirtyFiles) {
    console.error("Working tree is not clean. Commit or stash changes first.");
    console.error(dirtyFiles);
    process.exit(1);
  }

  if (!releaseBranches.includes(branch)) {
    console.error(`Must be on ${releaseBranches.join(" or ")} (currently on "${branch}").`);
    process.exit(1);
  }
}

// -- Read current version --

const pkgPath = join(ROOT, "package.json");
const pkg = JSON.parse(readFileSync(pkgPath, "utf-8"));
const currentVersion = pkg.version;

// -- Compute new version --

function bumpVersion(current, type) {
  const [major, minor, patch] = current.split(".").map(Number);
  switch (type) {
    case "major":
      return `${major + 1}.0.0`;
    case "minor":
      return `${major}.${minor + 1}.0`;
    case "patch":
      return `${major}.${minor}.${patch + 1}`;
    default:
      throw new Error(`Unknown bump type: ${type}`);
  }
}

const newVersion = isExplicitVersion ? bumpArg : bumpVersion(currentVersion, bumpArg);
const tag = `v${newVersion}`;
const today = new Date().toISOString().slice(0, 10);

// -- Changelog handling --

const changelogPath = join(ROOT, "CHANGELOG.md");
let changelog = existsSync(changelogPath) ? readFileSync(changelogPath, "utf-8") : "";

const unreleasedHeader = "## Unreleased";
const versionedHeader = `## ${newVersion} (${today})`;

let releaseNotes = "";

if (changelog.includes(unreleasedHeader)) {
  // Extract content between "## Unreleased" and the next "## " heading
  const unreleasedIdx = changelog.indexOf(unreleasedHeader);
  const afterUnreleased = unreleasedIdx + unreleasedHeader.length;
  const nextHeadingIdx = changelog.indexOf("\n## ", afterUnreleased);

  const rawNotes =
    nextHeadingIdx === -1
      ? changelog.slice(afterUnreleased)
      : changelog.slice(afterUnreleased, nextHeadingIdx);

  releaseNotes = rawNotes.trim();

  // Replace "## Unreleased" section with versioned header and add a new Unreleased
  const before = changelog.slice(0, unreleasedIdx);
  const after = nextHeadingIdx === -1 ? "" : changelog.slice(nextHeadingIdx);

  changelog = `${before}${unreleasedHeader}\n\n${versionedHeader}\n`;
  if (releaseNotes) {
    changelog += `\n${releaseNotes}\n`;
  }
  changelog += `${after}`;
}

// -- Dry-run output --

if (dryRun) {
  console.log("=== DRY RUN ===\n");
  console.log(`Version:   ${currentVersion} -> ${newVersion}`);
  console.log(`Tag:       ${tag}`);
  console.log(`Date:      ${today}`);
  console.log(`Branch:    ${branch}`);
  console.log();

  if (releaseNotes) {
    console.log("Release notes:");
    console.log("---");
    console.log(releaseNotes);
    console.log("---");
  } else {
    console.log("Release notes: (empty - no entries under ## Unreleased)");
  }

  console.log("\nActions that would be taken:");
  console.log("  1. Update package.json and package-lock.json");
  console.log("  2. Update CHANGELOG.md");
  console.log("  3. Write RELEASE_NOTES.md");
  console.log(`  4. git commit -m "chore: release ${tag}"`);
  console.log(`  5. git push origin ${branch}`);
  console.log(`  (CI will publish to the Marketplace and tag ${tag} after build/test passes)`);
  process.exit(0);
}

// -- Execute release --

console.log(`Releasing ${currentVersion} -> ${newVersion}\n`);

// 1. Bump version in package.json + package-lock.json (skip if already at target)
if (currentVersion !== newVersion) {
  console.log("Bumping version...");
  writeVersion(newVersion);
} else {
  console.log(`Version already at ${newVersion}, skipping bump.`);
}

function writeVersion(version) {
  const manifest = JSON.parse(readFileSync(pkgPath, "utf-8"));
  manifest.version = version;
  writeFileSync(pkgPath, JSON.stringify(manifest, null, 2) + "\n", "utf-8");

  const lockPath = join(ROOT, "package-lock.json");
  if (!existsSync(lockPath)) {
    return;
  }
  const lock = JSON.parse(readFileSync(lockPath, "utf-8"));
  lock.version = version;
  if (lock.packages?.[""]) {
    lock.packages[""].version = version;
  }
  writeFileSync(lockPath, JSON.stringify(lock, null, 2) + "\n", "utf-8");
}

// 2. Update CHANGELOG.md
if (existsSync(changelogPath)) {
  console.log("Updating CHANGELOG.md...");
  writeFileSync(changelogPath, changelog, "utf-8");
}

// 3. Write release notes file (used by CI for the GitHub Release body)
const releaseNotesPath = join(ROOT, "RELEASE_NOTES.md");
writeFileSync(releaseNotesPath, releaseNotes || `Release ${tag}`, "utf-8");
console.log("Wrote RELEASE_NOTES.md");

// 4. Stage + commit
console.log("Committing...");
git("add", "package.json", "package-lock.json", "CHANGELOG.md", "RELEASE_NOTES.md");
git("commit", "-m", `chore: release ${tag}`);

// 5. Push the current release branch
console.log(`Pushing ${branch} to origin...`);
git("push", "origin", "HEAD");

console.log(`\nDone! ${tag} pushed.`);
console.log("CI will build, test, publish to the Marketplace, tag and release it.");
