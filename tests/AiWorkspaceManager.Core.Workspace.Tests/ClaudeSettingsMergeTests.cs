// File: tests/AiWorkspaceManager.Core.Workspace.Tests/ClaudeSettingsMergeTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class ClaudeSettingsMergeTests
{
    private const string LegacyCommand = "python .ai/scripts/sync-platforms.py";
    private const string TemplateCommand = "bash .ai/scripts/hooks/py.sh .ai/scripts/hooks/stop_build.py";

    private static readonly string[] Legacy = [LegacyCommand];

    private const string Template = """
        {
          "plansDirectory": "./.ai/plans",
          "env": {
            "MSBUILDDISABLENODEREUSE": "1"
          },
          "hooks": {
            "Stop": [
              {
                "hooks": [
                  {
                    "type": "command",
                    "command": "bash .ai/scripts/hooks/py.sh .ai/scripts/hooks/stop_build.py"
                  }
                ]
              }
            ]
          },
          "permissions": {
            "allow": ["Bash(dotnet:*)"],
            "deny": ["Bash(rm -rf:*)"]
          }
        }
        """;

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_RemovesALegacyHookAddsTheTemplateHookAndKeepsTheProjectHook()
    {
        var target = """
            {
              "hooks": {
                "Stop": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "python .ai/scripts/sync-platforms.py"
                      },
                      {
                        "type": "command",
                        "command": "my-project-hook"
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        var text = result.Text;
        Assert.IsFalse(text.Contains(LegacyCommand, StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("my-project-hook", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains(TemplateCommand, StringComparison.Ordinal));
        var stop = JsoncMerger.ParseObject(text)!["hooks"]!["Stop"]!.AsArray();
        Assert.AreEqual(2, stop.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_DropsAGroupWhoseOnlyHookWasLegacy()
    {
        var target = """
            {
              "hooks": {
                "Stop": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "python .ai/scripts/sync-platforms.py"
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        var stop = JsoncMerger.ParseObject(result.Text)!["hooks"]!["Stop"]!.AsArray();
        Assert.AreEqual(1, stop.Count);
        Assert.AreEqual(TemplateCommand, stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_UnionsPermissionListsKeepingTheProjectsOrder()
    {
        var target = """
            {
              "permissions": {
                "allow": ["Bash(git:*)", "Bash(dotnet:*)"]
              }
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        var permissions = JsoncMerger.ParseObject(result.Text)!["permissions"]!.AsObject();
        var allow = permissions["allow"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        CollectionAssert.AreEqual(new[] { "Bash(git:*)", "Bash(dotnet:*)" }, allow);
        Assert.AreEqual("Bash(rm -rf:*)", permissions["deny"]![0]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_AddsMissingEnvKeysAndKeepsAConflictingProjectValue()
    {
        var target = """
            {
              "env": {
                "MSBUILDDISABLENODEREUSE": "0"
              }
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        CollectionAssert.AreEqual(new[] { "env.MSBUILDDISABLENODEREUSE" }, result.Conflicts.ToArray());
        Assert.AreEqual("0", JsoncMerger.ParseObject(result.Text)!["env"]!["MSBUILDDISABLENODEREUSE"]!.GetValue<string>());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_AddsAMissingTopLevelKeyButKeepsTheProjectsValue()
    {
        var target = """
            {
              "plansDirectory": "docs/plans"
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        CollectionAssert.AreEqual(new[] { "plansDirectory" }, result.Conflicts.ToArray());
        Assert.AreEqual("docs/plans", JsoncMerger.ParseObject(result.Text)!["plansDirectory"]!.GetValue<string>());
        Assert.IsTrue(JsoncMerger.ParseObject(result.Text)!.ContainsKey("hooks"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_IsIdempotent()
    {
        var target = """
            {
              // project settings
              "hooks": {
                "Stop": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "python .ai/scripts/sync-platforms.py"
                      }
                    ]
                  }
                ]
              },
            }
            """;
        var first = ClaudeSettingsMerge.Merge(target, Template, Legacy);

        var second = ClaudeSettingsMerge.Merge(first.Text, Template, Legacy);

        Assert.AreEqual(MergeStatus.Unchanged, second.Status);
        Assert.AreEqual(first.Text, second.Text);
        StringAssert.Contains(first.Text, "// project settings");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_CreatesTheFileWhenMissing()
    {
        var result = ClaudeSettingsMerge.Merge(null, Template, Legacy);

        Assert.AreEqual(MergeStatus.Created, result.Status);
        Assert.AreEqual(Template, result.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Merge_OnInvalidJsonReportsAnErrorAndWritesNothing()
    {
        var result = ClaudeSettingsMerge.Merge("{ \"hooks\": ", Template, Legacy);

        Assert.AreEqual(MergeStatus.Error, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }
}
