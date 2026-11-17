// File: tests/AiWorkspaceManager.Core.Workspace.Tests/VsCodeTasksMergeTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.

using System.Text.Json.Nodes;

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class VsCodeTasksMergeTests
{
    private const string Fragment = """
        {
          "version": "2.0.0",
          "tasks": [
            {
              "label": "Link .ai",
              "type": "shell",
              "command": "python",
              "args": [".ai/scripts/sync-platforms.py"]
            },
            {
              "label": "Validate",
              "type": "shell",
              "command": "python",
              "args": [".ai/tests/validate.py"]
            }
          ]
        }
        """;

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_MatchesTasksByLabelKeepsTheProjectsAndAppendsTheMissing()
    {
        var target = """
            {
              "version": "2.0.0",
              "tasks": [
                {
                  "label": "Link .ai",
                  "type": "shell",
                  "command": "my-own-command"
                }
              ]
            }
            """;

        var result = VsCodeTasksMerge.Merge(target, Fragment);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        CollectionAssert.AreEqual(new[] { "tasks[Validate]" }, result.Added.ToArray());
        var tasks = JsoncMerger.ParseObject(result.Text)!["tasks"]!.AsArray();
        Assert.AreEqual(2, tasks.Count);
        Assert.AreEqual("my-own-command", tasks[0]!["command"]!.GetValue<string>());
        Assert.AreEqual("Validate", tasks[1]!["label"]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_AddsVersionOnlyWhenAbsentAndNeverConflictsOnIt()
    {
        var target = """
            {
              "version": "1.0.0",
              "tasks": []
            }
            """;

        var result = VsCodeTasksMerge.Merge(target, Fragment);

        Assert.AreEqual("1.0.0", JsoncMerger.ParseObject(result.Text)!["version"]!.GetValue<string>());
        CollectionAssert.DoesNotContain(result.Conflicts.ToArray(), "version");
        Assert.AreEqual(2, JsoncMerger.ParseObject(result.Text)!["tasks"]!.AsArray().Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_AddsTheVersionWhenTheProjectHasNone()
    {
        var result = VsCodeTasksMerge.Merge("{ \"tasks\": [] }", Fragment);

        Assert.AreEqual("2.0.0", JsoncMerger.ParseObject(result.Text)!["version"]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_IsIdempotentAndCreatesAMissingFile()
    {
        var created = VsCodeTasksMerge.Merge(null, Fragment);
        var first = VsCodeTasksMerge.Merge("{\n  // project tasks\n  \"version\": \"2.0.0\",\n  \"tasks\": []\n}\n", Fragment);
        var second = VsCodeTasksMerge.Merge(first.Text, Fragment);

        Assert.AreEqual(MergeStatus.Created, created.Status);
        Assert.AreEqual(Fragment, created.Text);
        StringAssert.Contains(first.Text, "// project tasks");
        Assert.AreEqual(MergeStatus.Unchanged, second.Status);
        Assert.AreEqual(first.Text, second.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_OnInvalidJsonReportsAnErrorAndWritesNothing()
    {
        var result = VsCodeTasksMerge.Merge("{ \"tasks\": [", Fragment);

        Assert.AreEqual(MergeStatus.Error, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }
}
