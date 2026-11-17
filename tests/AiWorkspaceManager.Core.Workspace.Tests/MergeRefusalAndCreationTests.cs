// File: tests/AiWorkspaceManager.Core.Workspace.Tests/MergeRefusalAndCreationTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.

namespace AiWorkspaceManager.Core.Workspace.Tests;

/// <summary>
/// Rules the C# and Python merges share and that the parity corpus also covers: a file with a repeated key is
/// refused, a missing file is copied byte for byte, and a few layout rules for text surgery.
/// </summary>
[TestClass]
public sealed class MergeRefusalAndCreationTests
{
    private const string DuplicateKeys = "{\n  \"a\": 1,\n  \"a\": 2\n}\n";
    private static readonly HashSet<string> NoPins = new(StringComparer.Ordinal);

    [TestMethod]
    [TestCategory("Unit")]
    public void ParseObject_DuplicateKey_ReturnsNull()
    {
        Assert.IsNull(JsoncMerger.ParseObject(DuplicateKeys));
        Assert.IsNull(JsoncMerger.ParseObject("{\"env\": {\"A\": 1, \"A\": 2}}"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Constructor_DuplicateKey_Throws()
    {
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => new JsoncMerger(DuplicateKeys));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void VsCodeSettingsMerge_DuplicateKeyInTarget_IsInvalidAndWritesNothing()
    {
        var result = VsCodeSettingsMerge.Merge(DuplicateKeys, "{\"x\": 1}", NoPins);

        Assert.AreEqual(MergeStatus.Error, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void VsCodeTasksMerge_DuplicateKeyInTarget_IsInvalidAndWritesNothing()
    {
        var result = VsCodeTasksMerge.Merge(DuplicateKeys, "{\"version\": \"2.0.0\", \"tasks\": []}");

        Assert.AreEqual(MergeStatus.Error, result.Status);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClaudeSettingsMerge_DuplicateKeyInTarget_IsInvalidAndWritesNothing()
    {
        var result = ClaudeSettingsMerge.Merge("{\"env\": {}, \"env\": {}}", "{\"plansDirectory\": \"x\"}", []);

        Assert.AreEqual(MergeStatus.Error, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClaudeSettingsMerge_MissingFile_IsCopiedByteForByteWithEveryKey()
    {
        const string template = "{\r\n  \"plansDirectory\": \"./.ai/plans\",\r\n  \"enabledPlugins\": { \"x@y\": true }\r\n}\r\n";

        var result = ClaudeSettingsMerge.Merge(null, template, []);

        Assert.AreEqual(MergeStatus.Created, result.Status);
        Assert.AreEqual(template, result.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_AfterATrailingComma_KeepsTheTrailingCommaStyle()
    {
        var merger = new JsoncMerger("{\n  \"a\": 1,\n}\n");

        merger.AddMember([], "b", 2);

        Assert.AreEqual("{\n  \"a\": 1,\n  \"b\": 2,\n}\n", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_KeepsALineCommentWithTheMemberItTrails()
    {
        var merger = new JsoncMerger("{\n  \"a\": 1 // last\n}\n");

        merger.AddMember([], "b", 2);

        Assert.AreEqual("{\n  \"a\": 1, // last\n  \"b\": 2\n}\n", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_IntoAnObjectWithOnlyComments_PlacesTheMemberAfterThem()
    {
        var merger = new JsoncMerger("{\n  // nothing yet\n}\n");

        merger.AddMember([], "a", 1);

        Assert.AreEqual("{\n  // nothing yet\n  \"a\": 1\n}\n", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_First_PlacesTheMemberBeforeTheExistingOnes()
    {
        var merger = new JsoncMerger("{\n  \"tasks\": []\n}\n");

        merger.AddMember([], "version", "2.0.0", first: true);

        Assert.AreEqual("{\n  \"version\": \"2.0.0\",\n  \"tasks\": []\n}\n", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RemoveMember_RemovesTheWholeLineAndItsComma()
    {
        var merger = new JsoncMerger("{\n  \"a\": 1,\n  \"b\": 2\n}\n");

        merger.RemoveMember([], "a");

        Assert.AreEqual("{\n  \"b\": 2\n}\n", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClaudeSettingsMerge_EventLeftEmptyByLegacyRemovalIsRemovedNotLeftAsEmptyArray()
    {
        const string target = """
            {
              "hooks": {
                "Legacy": [
                  { "hooks": [ { "type": "command", "command": "old" } ] }
                ],
                "Custom": [
                  { "hooks": [ { "type": "command", "command": "mine" } ] }
                ]
              }
            }
            """;

        var result = ClaudeSettingsMerge.Merge(target, "{}", ["old"]);

        Assert.AreEqual(MergeStatus.Merged, result.Status);
        Assert.IsFalse(result.Text.Contains("Legacy", StringComparison.Ordinal), result.Text);
        Assert.IsTrue(result.Text.Contains("Custom", StringComparison.Ordinal));
    }
}
