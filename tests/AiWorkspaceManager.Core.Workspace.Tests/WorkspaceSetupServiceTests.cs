// File: tests/AiWorkspaceManager.Core.Workspace.Tests/WorkspaceSetupServiceTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.
//
// Setup is the upgrade path: vocabulary and tooling ownership, supporting folders, agent config,
// gitignore and the one-time catalog-layout migration. It must never overwrite a project file.

using System.Text.Json.Nodes;
using AiWorkspaceManager.Core.Workspace;
using AiWorkspaceManager.TestUtilities;

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class WorkspaceSetupServiceTests
{
    private static WorkspaceSetupService Subject(TempDirectory temp, out WorkspacePaths paths)
    {
        paths = new WorkspacePaths(Path.Combine(temp.Root, "project"));
        var queries = new WorkspaceQueries(paths, new GitSourceFetcher(paths, offline: true), offline: true);
        return new WorkspaceSetupService(queries);
    }

    private static JsonObject Manifest(params string[] clients) => new()
    {
        ["version"] = 1,
        ["sources"] = new JsonObject
        {
            ["template"] = new JsonObject { ["repo"] = "I-Synergy/AI", ["ref"] = "v1", ["path"] = "../template" },
        },
        ["clients"] = new JsonArray(clients.Select(client => (JsonNode)client).ToArray()),
        ["materialize"] = "copy",
    };

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallTooling_CopiesMissingVocabularyAndRecordsOwnership()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write("template/catalog/presets.json", "{\"presets\":{}}\n");
        temp.Write("template/.ai/scripts/sync-platforms.py", "# mirror\n");
        temp.Write("project/.ai/ai.json", Manifest("claude").ToJsonString());
        var sourceRoot = Path.Combine(temp.Root, "template");

        var first = setup.InstallTooling(sourceRoot);

        CollectionAssert.Contains(first.Copied.ToArray(), ".ai/catalog/presets.json");
        CollectionAssert.Contains(first.Copied.ToArray(), ".ai/scripts/sync-platforms.py");
        Assert.IsTrue(File.Exists(paths.ToolingRecordPath));
        var record = JsonNode.Parse(File.ReadAllText(paths.ToolingRecordPath))!.AsObject();
        var recorded = record["files"]!.AsArray().Select(entry => entry!["path"]!.GetValue<string>()).ToList();
        CollectionAssert.Contains(recorded, ".ai/catalog/presets.json");

        var second = setup.InstallTooling(sourceRoot);
        CollectionAssert.Contains(second.Skipped.ToArray(), ".ai/catalog/presets.json");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallTooling_ProtectsAFileTheProjectEdited()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write("template/catalog/presets.json", "{\"presets\":{}}\n");
        temp.Write("project/.ai/ai.json", Manifest("claude").ToJsonString());
        setup.InstallTooling(Path.Combine(temp.Root, "template"));
        var edited = Path.Combine(paths.Root, ".ai", "catalog", "presets.json");
        File.WriteAllText(edited, "{\"presets\":{\"mine\":{}}}\n");

        var outcome = setup.InstallTooling(Path.Combine(temp.Root, "template"), overwrite: true);

        CollectionAssert.Contains(outcome.Protected.ToArray(), ".ai/catalog/presets.json");
        Assert.AreEqual("{\"presets\":{\"mine\":{}}}\n", File.ReadAllText(edited));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallScaffold_OnlyAddsMissingFilesAndNeverOverwrites()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write("template/.ai/plans/example.md", "# plan\n");
        temp.Write("template/.ai/project/tech-stack.md", "# stack\n");
        temp.Write("template/.ai/session-context.md", "# session\n");
        temp.Write("project/.ai/reference/standards.md", "# ours\n");
        Directory.CreateDirectory(paths.AiDirectory);

        var first = setup.InstallScaffold(Path.Combine(temp.Root, "template"));
        var second = setup.InstallScaffold(Path.Combine(temp.Root, "template"));

        CollectionAssert.Contains(first.Copied.ToArray(), ".ai/plans/example.md");
        CollectionAssert.Contains(first.Copied.ToArray(), ".ai/session-context.md");
        Assert.IsEmpty(second.Copied);
        Assert.AreEqual("# ours\n", File.ReadAllText(Path.Combine(paths.Root, ".ai", "reference", "standards.md")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_WritesOnlyTheSelectedClientsFiles()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write("template/AGENTS.md", "# AGENTS\n");
        temp.Write("template/.gitattributes", "*.sh text eol=lf\n");
        temp.Write("template/.claude/settings.json", "{}\n");
        temp.Write("template/.github/copilot-instructions.md", "# Copilot\n");

        var summary = setup.InstallAgentConfig(Manifest("claude"));

        CollectionAssert.Contains(summary.Copied.ToArray(), "AGENTS.md");
        CollectionAssert.Contains(summary.Copied.ToArray(), ".claude/settings.json");
        CollectionAssert.DoesNotContain(summary.Copied.ToArray(), ".github/copilot-instructions.md");
        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".github", "copilot-instructions.md")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnsureGitignore_KeepsExistingLinesAndIsIdempotent()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(Path.Combine(paths.Root, ".gitignore"), "node_modules/\n.ai/tmp\n");

        var first = setup.EnsureGitignore();
        var second = setup.EnsureGitignore();

        Assert.AreEqual("updated", first);
        Assert.AreEqual("kept", second);
        var text = File.ReadAllText(Path.Combine(paths.Root, ".gitignore"));
        StringAssert.Contains(text, "node_modules/");
        Assert.AreEqual(1, text.Split(".ai/tmp").Length - 1);
        StringAssert.Contains(text, ".claude/skills/");
        StringAssert.Contains(text, ".agents/skills/");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MigrateCatalogDirectory_MovesARootCatalogForARemoteProject()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write(
            "project/.ai/ai.json",
            """
            {
              "version": 1,
              "sources": { "template": { "repo": "I-Synergy/AI", "ref": "v1", "resolve": "remote" } },
              "skills": []
            }
            """);
        temp.Write("project/catalog/presets.json", "{\"presets\":{}}\n");

        Assert.IsTrue(setup.MigrateCatalogDirectory());

        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, "catalog")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.AiDirectory, "catalog", "presets.json")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallTooling_SameTreeIsANoOp()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Root, "project");
        Directory.CreateDirectory(root);
        var paths = new WorkspacePaths(root);
        var queries = new WorkspaceQueries(paths, new GitSourceFetcher(paths, offline: true), offline: true);
        var setup = new WorkspaceSetupService(queries);

        var summary = setup.InstallTooling(root);

        Assert.IsEmpty(summary.Copied);
        Assert.IsEmpty(summary.Missing);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallTooling_FallsBackToTheBundledVocabulary()
    {
        using var temp = new TempDirectory();
        var paths = new WorkspacePaths(Path.Combine(temp.Root, "project"));
        var bundle = Path.Combine(temp.Root, "bundle");
        temp.Write("bundle/catalog/presets.json", "{\"presets\":{}}\n");
        temp.Write("project/.ai/ai.json", Manifest("claude").ToJsonString());
        var queries = new WorkspaceQueries(paths, new GitSourceFetcher(paths, offline: true), offline: true, bundle);
        var setup = new WorkspaceSetupService(queries);

        var summary = setup.InstallTooling(Path.Combine(temp.Root, "template"));

        CollectionAssert.Contains(summary.Copied.ToArray(), ".ai/catalog/presets.json");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnsureGitignore_CreatesTheFileWhenItDoesNotExist()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        Directory.CreateDirectory(paths.Root);

        Assert.AreEqual("created", setup.EnsureGitignore());
        StringAssert.Contains(File.ReadAllText(Path.Combine(paths.Root, ".gitignore")), ".ai/tmp/");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnsureGitignore_IgnoresGeneratedClaudeRulesAndKeepsGithubHooks()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        Directory.CreateDirectory(paths.Root);

        setup.EnsureGitignore();

        var text = File.ReadAllText(Path.Combine(paths.Root, ".gitignore"));
        StringAssert.Contains(text, ".claude/rules/*.instructions.md");
        StringAssert.Contains(text, ".github/hooks/");
    }

    private static void WriteTemplateVsCode(TempDirectory temp)
    {
        temp.Write("template/.ai/reference/templates/merge-rules.json", """
            {
              "version": 1,
              "vscode": { "pinnedSettings": ["chat.useAgentsMdFile"] },
              "claude": { "legacyHookCommands": ["python .ai/scripts/sync-platforms.py"] }
            }
            """);
        temp.Write("template/.ai/reference/templates/vscode/settings.json", """
            {
              "chat.useAgentsMdFile": false,
              "chat.useAgentSkills": true
            }
            """);
        temp.Write("template/.ai/reference/templates/vscode/tasks.json", """
            {
              "version": "2.0.0",
              "tasks": [ { "label": "Link .ai", "type": "shell", "command": "python" } ]
            }
            """);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_CreatesMissingVsCodeFilesFromTheFragments()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        WriteTemplateVsCode(temp);

        var summary = setup.InstallAgentConfig(Manifest("copilot"));

        CollectionAssert.Contains(summary.Copied.ToArray(), ".vscode/settings.json");
        CollectionAssert.Contains(summary.Copied.ToArray(), ".vscode/tasks.json");
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, ".vscode", "settings.json")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, ".vscode", "tasks.json")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_MergesAnExistingVsCodeSettingsFileAndPreservesItsComments()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        WriteTemplateVsCode(temp);
        temp.Write("project/.vscode/settings.json", "{\n  // mine\n  \"chat.useAgentsMdFile\": true,\n}\n");

        var summary = setup.InstallAgentConfig(Manifest("copilot"));

        var text = File.ReadAllText(Path.Combine(paths.Root, ".vscode", "settings.json"));
        StringAssert.Contains(text, "// mine");
        Assert.IsFalse(JsoncMerger.ParseObject(text)!["chat.useAgentsMdFile"]!.GetValue<bool>());
        Assert.IsTrue(JsoncMerger.ParseObject(text)!["chat.useAgentSkills"]!.GetValue<bool>());
        var merged = summary.Merged.Single(file => file.RelativePath == ".vscode/settings.json");
        Assert.AreEqual(MergeStatus.Merged, merged.Result.Status);
        CollectionAssert.Contains(merged.Result.Pinned.ToArray(), "chat.useAgentsMdFile");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_IsIdempotent()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        WriteTemplateVsCode(temp);
        setup.InstallAgentConfig(Manifest("copilot"));
        var settings = Path.Combine(paths.Root, ".vscode", "settings.json");
        var before = File.ReadAllBytes(settings);

        var second = setup.InstallAgentConfig(Manifest("copilot"));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(settings));
        Assert.IsEmpty(second.Copied);
        Assert.IsTrue(second.Merged.All(file => file.Result.Status == MergeStatus.Unchanged));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_SkipsTheVsCodeMergeWhenTheTemplateHasNoMergeRules()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        temp.Write("template/.ai/reference/templates/vscode/settings.json", "{ \"chat.useAgentSkills\": true }\n");
        temp.Write("template/.vscode/settings.json", "{ \"python.testing.pytestEnabled\": true }\n");

        var summary = setup.InstallAgentConfig(Manifest("copilot"));

        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".vscode", "settings.json")));
        Assert.IsEmpty(summary.Merged);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_LeavesVsCodeAloneWhenCopilotIsNotSelected()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        WriteTemplateVsCode(temp);

        var summary = setup.InstallAgentConfig(Manifest("claude"));

        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".vscode", "settings.json")));
        Assert.IsFalse(summary.Merged.Any(file => file.RelativePath.StartsWith(".vscode/", StringComparison.Ordinal)));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void InstallAgentConfig_MergesTheClaudeSettingsOnlyForTheClaudeClient()
    {
        using var temp = new TempDirectory();
        var setup = Subject(temp, out var paths);
        WriteTemplateVsCode(temp);
        temp.Write("template/.claude/settings.json", """
            {
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "command", "command": "bash .ai/scripts/hooks/py.sh stop_build.py" } ] } ]
              }
            }
            """);
        temp.Write("project/.claude/settings.json", """
            {
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "command", "command": "python .ai/scripts/sync-platforms.py" } ] } ]
              }
            }
            """);

        var copilotOnly = setup.InstallAgentConfig(Manifest("copilot"));
        var untouched = File.ReadAllText(Path.Combine(paths.Root, ".claude", "settings.json"));
        var summary = setup.InstallAgentConfig(Manifest("claude"));

        Assert.IsFalse(copilotOnly.Merged.Any(file => file.RelativePath == ".claude/settings.json"));
        StringAssert.Contains(untouched, "sync-platforms.py");
        var text = File.ReadAllText(Path.Combine(paths.Root, ".claude", "settings.json"));
        Assert.IsFalse(text.Contains("sync-platforms.py", StringComparison.Ordinal));
        StringAssert.Contains(text, "stop_build.py");
        Assert.AreEqual(MergeStatus.Merged, summary.Merged.Single(file => file.RelativePath == ".claude/settings.json").Result.Status);
    }
}
