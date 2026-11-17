// File: tests/AiWorkspaceManager.Core.Workspace.Tests/MergeParityTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.

using System.Text;
using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace.Tests;

/// <summary>
/// Proves the C# merge produces byte-identical output to the template's Python merge
/// (<c>.ai/scripts/upgrade-template.py</c>). Every case in <c>Fixtures/MergeParity</c> holds an input file and the
/// expected file the Python implementation wrote; a case without an expected file is one both sides must refuse.
/// The fixtures are byte-exact copies, so the test does not depend on the template repository.
/// </summary>
[TestClass]
public sealed class MergeParityTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MergeParity");

    public static IEnumerable<object[]> Cases() =>
        ((JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "manifest.json")))!)
            .Select(node => new object[] { node!["name"]!.GetValue<string>() });

    [TestMethod]
    [TestCategory("Unit")]
    [DynamicData(nameof(Cases))]
    public void Merge_MatchesThePythonImplementationByteForByte(string name)
    {
        var (kind, expected, input, pythonStatus) = Load(name);

        var first = Run(kind, input);

        if (pythonStatus == "error")
        {
            Assert.AreEqual(MergeStatus.Error, first.Status, $"{name}: the Python merge refuses this input, so must C#");
            Assert.IsNotNull(first.Error);
            return;
        }

        if (pythonStatus == "unchanged")
        {
            Assert.AreEqual(MergeStatus.Unchanged, first.Status, $"{name}: Python writes nothing, so C# must not either");
            return;
        }

        Assert.IsTrue(first.Status is MergeStatus.Created or MergeStatus.Merged, $"{name}: status was {first.Status}");
        var written = Write(first.Text);
        Assert.IsTrue(
            expected!.SequenceEqual(written),
            $"{name}: output differs from Python.{Environment.NewLine}--- python{Environment.NewLine}{Visible(expected!)}{Environment.NewLine}--- csharp{Environment.NewLine}{Visible(written)}");

        var second = Run(kind, written);
        Assert.AreEqual(MergeStatus.Unchanged, second.Status, $"{name}: a second run must change nothing");
    }

    private static (string Kind, byte[]? Expected, byte[]? Input, string PythonStatus) Load(string name)
    {
        var manifest = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "manifest.json")))!;
        var entry = manifest.Single(node => node!["name"]!.GetValue<string>() == name)!;
        var kind = entry["kind"]!.GetValue<string>();
        var file = kind == "tasks" ? "tasks.json" : "settings.json";
        var directory = Path.Combine(FixtureRoot, "cases", name);
        var input = Path.Combine(directory, "input-" + file);
        var expected = Path.Combine(directory, "expected-" + file);
        return (
            kind,
            File.Exists(expected) ? File.ReadAllBytes(expected) : null,
            File.Exists(input) ? File.ReadAllBytes(input) : null,
            entry["status"]!.GetValue<string>());
    }

    // Mirrors WorkspaceSetupService: the project file is decoded keeping any byte-order mark, and written without adding one.
    private static JsoncMergeResult Run(string kind, byte[]? input)
    {
        var target = input is null ? null : Encoding.UTF8.GetString(input);
        var templates = Path.Combine(FixtureRoot, "templates");
        var rules = MergeRules.Load(RulesRoot())!;
        return kind switch
        {
            "settings" => VsCodeSettingsMerge.Merge(
                target,
                File.ReadAllText(Path.Combine(templates, "settings.json")),
                new HashSet<string>(rules.PinnedSettings, StringComparer.Ordinal)),
            "tasks" => VsCodeTasksMerge.Merge(target, File.ReadAllText(Path.Combine(templates, "tasks.json"))),
            "claude" => ClaudeSettingsMerge.Merge(
                target,
                File.ReadAllText(Path.Combine(templates, "claude-settings.json")),
                rules.LegacyHookCommands),
            _ => throw new InvalidOperationException(kind),
        };
    }

    // MergeRules.Load expects a template tree, so lay the fixture's rules file out as one.
    private static string RulesRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "merge-parity-rules");
        var directory = Path.Combine(root, ".ai", "reference", "templates");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(FixtureRoot, "templates", "merge-rules.json"), Path.Combine(directory, "merge-rules.json"), overwrite: true);
        return root;
    }

    private static byte[] Write(string text) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    private static string Visible(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Replace("\r", "\r").Replace("\ufeff", "<BOM>");
}
