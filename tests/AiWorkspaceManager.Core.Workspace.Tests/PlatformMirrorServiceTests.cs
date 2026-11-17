// File: tests/AiWorkspaceManager.Core.Workspace.Tests/PlatformMirrorServiceTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.
//
// Client folders are links onto .ai/; disabling a client removes only the links it created.

using System.Text.Json.Nodes;
using AiWorkspaceManager.Core.Workspace;
using AiWorkspaceManager.TestUtilities;

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class PlatformMirrorServiceTests
{
    private static (TempDirectory Temp, WorkspacePaths Paths, PlatformMirrorService Service) Scenario(params string[] clients)
    {
        var temp = new TempDirectory();
        temp.Write("project/.ai/skills/alpha/SKILL.md", "---\nname: alpha\n---\n");
        temp.Write("project/.ai/agents/lead.md", "---\nname: lead\n---\n");
        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["sources"] = JsonObject.Parse("{\"template\":{\"repo\":\"I-Synergy/AI\",\"ref\":\"local\"}}"),
            ["clients"] = new JsonArray(clients.Select(client => (JsonNode)client).ToArray()),
        };
        temp.Write("project/.ai/ai.json", manifest.ToJsonString());
        var paths = new WorkspacePaths(Path.Combine(temp.Root, "project"));
        return (temp, paths, new PlatformMirrorService(paths));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_CreatesTheSelectedClientsJunctions()
    {
        var (temp, paths, service) = Scenario("claude", "agents");
        using var scope = temp;
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;

        var report = service.Sync(manifest);

        Assert.IsTrue(report.Changed > 0);
        Assert.IsTrue(PathOps.IsLink(Path.Combine(paths.Root, ".claude", "skills")));
        Assert.IsTrue(PathOps.IsLink(Path.Combine(paths.Root, ".agents", "skills")));
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, ".github", "skills")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RemovesOnlyTheDisabledClientsJunctions()
    {
        var (temp, paths, service) = Scenario("claude", "copilot");
        using var scope = temp;
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;
        _ = service.Sync(manifest);
        File.WriteAllText(Path.Combine(paths.Root, ".github", "workflow.yml"), "name: ci\n");
        manifest.Set("clients", new JsonArray(JsonValue.Create("claude")));
        manifest.Save();

        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsTrue(PathOps.IsLink(Path.Combine(paths.Root, ".claude", "skills")));
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, ".github", "skills")));
        Assert.IsTrue(Directory.Exists(Path.Combine(paths.Root, ".github")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_PrunesAnEmptyDisabledClientDirectory()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;
        _ = service.Sync(manifest);
        manifest.Set("clients", new JsonArray());
        manifest.Save();

        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, ".claude")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_ReportsAConflictInsteadOfDeletingRealFiles()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        var target = Path.Combine(paths.Root, ".claude", "skills");
        temp.Write("project/.claude/skills/alpha/SKILL.md", "---\nname: alpha\n---\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;

        var report = service.Sync(manifest);

        CollectionAssert.Contains(report.Conflicts.ToArray(), ".claude/skills");
        Assert.IsFalse(PathOps.IsLink(target));
        Assert.IsTrue(File.Exists(Path.Combine(target, "alpha", "SKILL.md")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_LinksEachHookIntoTheCopilotHookFolder()
    {
        var (temp, paths, service) = Scenario("copilot");
        using var scope = temp;
        temp.Write(
            "project/.ai/hooks/guard/hooks.json",
            "{\"version\":1,\"hooks\":{\"preToolUse\":[]}}\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;

        var report = service.Sync(manifest);

        var target = Path.Combine(paths.Root, ".github", "hooks", "guard.json");
        Assert.IsTrue(File.Exists(target));
        Assert.AreEqual(
            File.ReadAllText(Path.Combine(paths.Root, ".ai", "hooks", "guard", "hooks.json")),
            File.ReadAllText(target));
        CollectionAssert.DoesNotContain(report.Conflicts.ToArray(), ".github/hooks/guard.json");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_LeavesHookFilesAloneWhenCopilotIsDisabled()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        temp.Write("project/.ai/hooks/guard/hooks.json", "{}\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;

        _ = service.Sync(manifest);

        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".github", "hooks", "guard.json")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RemovesTheHookLinkOfAnUninstalledHook()
    {
        var (temp, paths, service) = Scenario("copilot");
        using var scope = temp;
        temp.Write("project/.ai/hooks/guard/hooks.json", "{}\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;
        _ = service.Sync(manifest);
        var target = Path.Combine(paths.Root, ".github", "hooks", "guard.json");
        Assert.IsTrue(File.Exists(target));

        Directory.Delete(Path.Combine(paths.Root, ".ai", "hooks", "guard"), recursive: true);
        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(PathOps.ExistsOrLink(target));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_ReportsAConflictingHookFileWithoutOverwritingIt()
    {
        var (temp, paths, service) = Scenario("copilot");
        using var scope = temp;
        temp.Write("project/.ai/hooks/guard/hooks.json", "{\"version\":1}\n");
        temp.Write("project/.github/hooks/guard.json", "{\"hand\":\"written\"}\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;

        var report = service.Sync(manifest);

        CollectionAssert.Contains(report.Conflicts.ToArray(), ".github/hooks/guard.json");
        Assert.AreEqual(
            "{\"hand\":\"written\"}\n",
            File.ReadAllText(Path.Combine(paths.Root, ".github", "hooks", "guard.json")));
    }

    private const string CsharpSource = "---\ndescription: C# coding rules\napplyTo: \"**/*.cs\"\n---\n# C#\n\nUse records.\n";
    private const string CsharpRule = "---\ndescription: \"C# coding rules\"\npaths:\n  - \"**/*.cs\"\n---\n"
        + "<!-- Generated from .ai/instructions/csharp.instructions.md by sync-platforms.py. Edit the source, not this file. -->\n"
        + "# C#\n\nUse records.\n";
    private const string AllSource = "---\napplyTo: '**/*'\n---\nBody for every file.\n";
    private const string AllRule = "<!-- Generated from .ai/instructions/all.instructions.md by sync-platforms.py. Edit the source, not this file. -->\n"
        + "Body for every file.\n";
    private const string PlainSource = "No frontmatter at all.\n";
    private const string PlainRule = "<!-- Generated from .ai/instructions/plain.instructions.md by sync-platforms.py. Edit the source, not this file. -->\n"
        + "No frontmatter at all.\n";

    /// <summary>CRLF source with an escaped quote in the description and two globs; the rule must be LF and byte-identical to the template.</summary>
    private const string MultiSource = "---\r\ndescription: \"Quoted \\\"text\\\" here\"\r\napplyTo: \"**/*.ts, **/*.tsx\"\r\nname: ignored\r\n---\r\nLine one\r\nLine two\r\n";
    private const string MultiRule = "---\ndescription: \"Quoted \\\\\\\"text\\\\\\\" here\"\npaths:\n  - \"**/*.ts\"\n  - \"**/*.tsx\"\n---\n"
        + "<!-- Generated from .ai/instructions/multi.instructions.md by sync-platforms.py. Edit the source, not this file. -->\n"
        + "Line one\nLine two\n";

    private static void WriteProject(WorkspacePaths paths, string relative, string text)
    {
        var path = Path.Combine(paths.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string ProjectFile(WorkspacePaths paths, string relative) =>
        File.ReadAllText(Path.Combine(paths.Root, relative));

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_GeneratesClaudeRulesByteIdenticalToTheTemplateRenderer()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        WriteProject(paths, ".ai/instructions/all.instructions.md", AllSource);
        WriteProject(paths, ".ai/instructions/multi.instructions.md", MultiSource);
        WriteProject(paths, ".ai/instructions/plain.instructions.md", PlainSource);

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.AreEqual(CsharpRule, ProjectFile(paths, ".claude/rules/csharp.instructions.md"));
        Assert.AreEqual(AllRule, ProjectFile(paths, ".claude/rules/all.instructions.md"));
        Assert.AreEqual(MultiRule, ProjectFile(paths, ".claude/rules/multi.instructions.md"));
        Assert.AreEqual(PlainRule, ProjectFile(paths, ".claude/rules/plain.instructions.md"));
        CollectionAssert.Contains(
            report.Lines.ToArray(),
            "  WROTE .claude/rules/csharp.instructions.md (from .ai/instructions/csharp.instructions.md)");
        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, ".claude", "instructions")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_ReportsAnUnchangedGeneratedRuleOnTheSecondRun()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        CollectionAssert.Contains(report.Lines.ToArray(), "  OK       .claude/rules/csharp.instructions.md");
        Assert.AreEqual(CsharpRule, ProjectFile(paths, ".claude/rules/csharp.instructions.md"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_LeavesAHandWrittenRuleWithTheSameNameUntouched()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        WriteProject(paths, ".claude/rules/csharp.instructions.md", "# my own rule\n");

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        CollectionAssert.Contains(report.Conflicts.ToArray(), ".claude/rules/csharp.instructions.md");
        Assert.AreEqual("# my own rule\n", ProjectFile(paths, ".claude/rules/csharp.instructions.md"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RemovesAGeneratedRuleWhoseSourceIsGoneButKeepsHandWrittenRules()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        WriteProject(paths, ".ai/instructions/gone.instructions.md", PlainSource);
        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);
        WriteProject(paths, ".claude/rules/mine.instructions.md", "# mine\n");
        File.Delete(Path.Combine(paths.Root, ".ai", "instructions", "gone.instructions.md"));

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".claude", "rules", "gone.instructions.md")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, ".claude", "rules", "csharp.instructions.md")));
        Assert.AreEqual("# mine\n", ProjectFile(paths, ".claude/rules/mine.instructions.md"));
        CollectionAssert.Contains(
            report.Lines.ToArray(),
            "  REMOVED .claude/rules/gone.instructions.md (source removed)");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RemovesGeneratedRulesWhenClaudeIsDisabled()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);
        WriteProject(paths, ".claude/rules/mine.instructions.md", "# mine\n");
        var manifest = ManifestDocument.Load(paths.ManifestPath)!;
        manifest.Set("clients", new JsonArray(JsonValue.Create("copilot")));
        manifest.Save();

        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(File.Exists(Path.Combine(paths.Root, ".claude", "rules", "csharp.instructions.md")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, ".claude", "rules", "mine.instructions.md")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RefusesToWriteRulesThroughALinkedRulesFolder()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        Directory.CreateDirectory(Path.Combine(paths.Root, ".claude"));
        Assert.IsTrue(Materializer.CreateLink(
            Path.Combine(paths.Root, ".claude", "rules"),
            Path.Combine(paths.Root, ".ai", "instructions")));

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        CollectionAssert.Contains(report.Conflicts.ToArray(), ".claude/rules");
        Assert.AreEqual(CsharpSource, ProjectFile(paths, ".ai/instructions/csharp.instructions.md"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RetiresTheLegacyClaudeInstructionsLink()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        WriteProject(paths, ".ai/instructions/csharp.instructions.md", CsharpSource);
        var legacy = Path.Combine(paths.Root, ".claude", "instructions");
        Directory.CreateDirectory(Path.Combine(paths.Root, ".claude"));
        Assert.IsTrue(Materializer.CreateLink(legacy, Path.Combine(paths.Root, ".ai", "instructions")));

        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(PathOps.ExistsOrLink(legacy));
        CollectionAssert.Contains(
            report.Lines.ToArray(),
            "  REMOVED .claude/instructions (retired adapter)");
        Assert.IsTrue(File.Exists(Path.Combine(paths.Root, ".ai", "instructions", "csharp.instructions.md")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_RemovesAnEmptyLegacyClaudeInstructionsDirectoryButKeepsRealContent()
    {
        var (temp, paths, service) = Scenario("claude");
        using var scope = temp;
        Directory.CreateDirectory(Path.Combine(paths.Root, ".claude", "instructions"));

        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(Directory.Exists(Path.Combine(paths.Root, ".claude", "instructions")));

        WriteProject(paths, ".claude/instructions/notes.md", "keep\n");
        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        CollectionAssert.Contains(report.Conflicts.ToArray(), ".claude/instructions");
        Assert.AreEqual("keep\n", ProjectFile(paths, ".claude/instructions/notes.md"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Sync_LinksPromptsOnlyWhilePromptsHoldFiles()
    {
        var (temp, paths, service) = Scenario("claude", "copilot");
        using var scope = temp;
        var commands = Path.Combine(paths.Root, ".claude", "commands");
        var prompts = Path.Combine(paths.Root, ".github", "prompts");

        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(PathOps.ExistsOrLink(commands));
        Assert.IsFalse(PathOps.ExistsOrLink(prompts));

        WriteProject(paths, ".ai/prompts/review.prompt.md", "Review the change.\n");
        _ = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsTrue(PathOps.IsLink(commands));
        Assert.IsTrue(PathOps.IsLink(prompts));

        File.Delete(Path.Combine(paths.Root, ".ai", "prompts", "review.prompt.md"));
        var report = service.Sync(ManifestDocument.Load(paths.ManifestPath)!);

        Assert.IsFalse(PathOps.ExistsOrLink(commands));
        Assert.IsFalse(PathOps.ExistsOrLink(prompts));
        CollectionAssert.Contains(
            report.Lines.ToArray(),
            "  REMOVED .claude/commands (no .ai/prompts files)");
    }
}
