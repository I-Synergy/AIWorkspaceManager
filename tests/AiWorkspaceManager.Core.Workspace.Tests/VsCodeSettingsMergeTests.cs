// File: tests/AiWorkspaceManager.Core.Workspace.Tests/VsCodeSettingsMergeTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class VsCodeSettingsMergeTests
{
    private const string Fragment = """
        {
          "terminal.integrated.env.windows": {
            "PIP_CONFIG_FILE": "${workspaceFolder}/pip.ini",
            "MSBUILDDISABLENODEREUSE": "1"
          },
          "chat.useAgentsMdFile": false,
          "chat.useAgentSkills": true,
          "editor.formatOnSave": true
        }
        """;

    private static readonly HashSet<string> Pinned = new(StringComparer.Ordinal) { "chat.useAgentsMdFile" };

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_CreatesTheFileFromTheFragmentWhenMissing()
    {
        var result = VsCodeSettingsMerge.Merge(null, Fragment, Pinned);

        Assert.AreEqual(MergeStatus.Created, result.Status);
        Assert.AreEqual(Fragment, result.Text);
        CollectionAssert.Contains(result.Added.ToArray(), "chat.useAgentSkills");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_GivesNestedEnvironmentTheMissingKeysAndKeepsTheProjectsOwn()
    {
        var target = """
            {
              "terminal.integrated.env.windows": {
                "PIP_CONFIG_FILE": "${workspaceFolder}/pip.ini"
              }
            }
            """;

        var result = VsCodeSettingsMerge.Merge(target, Fragment, Pinned);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        CollectionAssert.Contains(result.Added.ToArray(), "terminal.integrated.env.windows.MSBUILDDISABLENODEREUSE");
        var env = JsoncMerger.ParseObject(result.Text)!["terminal.integrated.env.windows"]!.AsObject();
        Assert.AreEqual("${workspaceFolder}/pip.ini", env["PIP_CONFIG_FILE"]!.GetValue<string>());
        Assert.AreEqual("1", env["MSBUILDDISABLENODEREUSE"]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_ReplacesAPinnedKeyWithTheTemplateValue()
    {
        var target = """
            {
              "chat.useAgentsMdFile": true
            }
            """;

        var result = VsCodeSettingsMerge.Merge(target, Fragment, Pinned);

        CollectionAssert.AreEqual(new[] { "chat.useAgentsMdFile" }, result.Pinned.ToArray());
        Assert.IsFalse(JsoncMerger.ParseObject(result.Text)!["chat.useAgentsMdFile"]!.GetValue<bool>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_KeepsAProjectValueOverANonPinnedConflictAndReportsIt()
    {
        var target = """
            {
              // the team turned format-on-save off
              "editor.formatOnSave": false
            }
            """;

        var result = VsCodeSettingsMerge.Merge(target, Fragment, Pinned);

        CollectionAssert.AreEqual(new[] { "editor.formatOnSave" }, result.Conflicts.ToArray());
        StringAssert.Contains(result.Text, "// the team turned format-on-save off");
        Assert.IsFalse(JsoncMerger.ParseObject(result.Text)!["editor.formatOnSave"]!.GetValue<bool>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_OnAnEmptyObjectExpandsItOntoLines()
    {
        var result = VsCodeSettingsMerge.Merge("{}", "{\"chat.useAgentSkills\": true}", Pinned);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        Assert.AreEqual("{\n    \"chat.useAgentSkills\": true\n}", result.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_OnAFileWithoutTrailingNewlineKeepsItWithout()
    {
        var result = VsCodeSettingsMerge.Merge("{\n  \"editor.formatOnSave\": true\n}", Fragment, Pinned);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        Assert.IsFalse(result.Text.EndsWith('\n'));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_IsIdempotent()
    {
        var target = """
            {
              // mine
              "editor.formatOnSave": false,
            }
            """;
        var first = VsCodeSettingsMerge.Merge(target, Fragment, Pinned);

        var second = VsCodeSettingsMerge.Merge(first.Text, Fragment, Pinned);

        Assert.AreEqual(MergeStatus.Unchanged, second.Status);
        Assert.AreEqual(first.Text, second.Text);
        Assert.IsEmpty(second.Added);
        Assert.IsEmpty(second.Pinned);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_OnInvalidJsonReportsAnErrorAndWritesNothing()
    {
        var result = VsCodeSettingsMerge.Merge("{ \"editor.formatOnSave\": ", Fragment, Pinned);

        Assert.AreEqual(MergeStatus.Error, result.Status);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(string.Empty, result.Text);
    }
}
