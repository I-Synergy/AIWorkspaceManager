// File: tests/AiWorkspaceManager.Core.Workspace.Tests/JsoncMergerTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.
//
// The text-surgery editor behind every settings merge: it must change only the spans it targets, so comments,
// trailing commas, ordering and formatting of the project's file survive.

using System.Text.Json;

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class JsoncMergerTests
{
    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_KeepsCommentsAndTrailingCommas()
    {
        var merger = new JsoncMerger(Lines(
            "{",
            "  // Project settings",
            "  \"editor.tabSize\": 2, // keep",
            "  \"files.eol\": \"\\n\",",
            "}"));

        var added = merger.AddMember([], "chat.useAgentSkills", true);

        Assert.IsTrue(added);
        Assert.AreEqual(Lines(
            "{",
            "  // Project settings",
            "  \"editor.tabSize\": 2, // keep",
            "  \"files.eol\": \"\\n\",",
            "  \"chat.useAgentSkills\": true,",
            "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_AddsTheMissingCommaBeforeACommentOnTheLastValue()
    {
        var merger = new JsoncMerger(Lines("{", "  \"a\": 1 // note", "}"));

        merger.AddMember([], "b", 2);

        Assert.AreEqual(Lines("{", "  \"a\": 1, // note", "  \"b\": 2", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_ReturnsFalseWhenTheNameAlreadyExists()
    {
        var text = Lines("{", "  \"a\": 1", "}");
        var merger = new JsoncMerger(text);

        Assert.IsFalse(merger.AddMember([], "a", 2));
        Assert.IsFalse(merger.Changed);
        Assert.AreEqual(text, merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ReplaceMember_ReplacesOnlyTheValueAndKeepsTheTrailingComment()
    {
        var merger = new JsoncMerger(Lines("{", "  \"chat.useAgentsMdFile\": true, // mine", "}"));

        Assert.IsTrue(merger.ReplaceMember([], "chat.useAgentsMdFile", false));

        Assert.AreEqual(Lines("{", "  \"chat.useAgentsMdFile\": false, // mine", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_NestedObjectGainsOnlyTheMissingKey()
    {
        var merger = new JsoncMerger(Lines(
            "{",
            "  \"terminal.integrated.env.windows\": {",
            "    \"PIP_CONFIG_FILE\": \"${workspaceFolder}/pip.ini\"",
            "  }",
            "}"));

        merger.AddMember(["terminal.integrated.env.windows"], "MSBUILDDISABLENODEREUSE", "1");

        Assert.AreEqual(Lines(
            "{",
            "  \"terminal.integrated.env.windows\": {",
            "    \"PIP_CONFIG_FILE\": \"${workspaceFolder}/pip.ini\",",
            "    \"MSBUILDDISABLENODEREUSE\": \"1\"",
            "  }",
            "}"), merger.Text);
        Assert.IsNotNull(JsoncMerger.ParseObject(merger.Text));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_IntoAnEmptyMultilineObjectIndentsLikeTheSiblings()
    {
        var merger = new JsoncMerger(Lines("{", "  \"env\": {", "  }", "}"));

        merger.AddMember(["env"], "A", "1");

        Assert.AreEqual(Lines("{", "  \"env\": {", "    \"A\": \"1\"", "  }", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_IntoAnEmptyInlineObjectExpandsOntoLinesLikeTheTemplateMerge()
    {
        var merger = new JsoncMerger("{\"env\": {}}");

        merger.AddMember(["env"], "A", "1");

        Assert.AreEqual("{\"env\": {\n    \"A\": \"1\"\n}}", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AppendElement_IntoAnEmptyArrayExpandsOntoLinesLikeTheTemplateMerge()
    {
        var merger = new JsoncMerger("{\"list\": []}");

        merger.AppendElement(["list"], "x");

        Assert.AreEqual("{\"list\": [\n    \"x\"\n]}", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AppendElement_AddsAfterTheLastElementWithoutDuplicatingTheComma()
    {
        var merger = new JsoncMerger(Lines("{", "  \"list\": [", "    \"a\",", "  ]", "}"));

        merger.AppendElement(["list"], "b");

        Assert.AreEqual(Lines("{", "  \"list\": [", "    \"a\",", "    \"b\",", "  ]", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void AddMember_ToADocumentWithoutTrailingNewlineAddsNoNewline()
    {
        var merger = new JsoncMerger("{\n  \"a\": 1\n}");

        merger.AddMember([], "b", 2);

        Assert.AreEqual("{\n  \"a\": 1,\n  \"b\": 2\n}", merger.Text);
        Assert.IsFalse(merger.Text.EndsWith('\n'));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RemoveElement_RemovesTheWholeLineOfAnElementFollowedByAComma()
    {
        var merger = new JsoncMerger(Lines("{", "  \"hooks\": [", "    \"a\",", "    \"b\"", "  ]", "}"));

        merger.RemoveElement(["hooks"], 0);

        Assert.AreEqual(Lines("{", "  \"hooks\": [", "    \"b\"", "  ]", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RemoveElement_RemovesTheSeparatorBeforeTheLastElement()
    {
        var merger = new JsoncMerger(Lines("{", "  \"hooks\": [", "    \"a\",", "    \"b\"", "  ]", "}"));

        merger.RemoveElement(["hooks"], 1);

        Assert.AreEqual(Lines("{", "  \"hooks\": [", "    \"a\"", "  ]", "}"), merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RemoveElement_ReachesNestedArraysThroughIndexSteps()
    {
        var merger = new JsoncMerger("{\"groups\": [{\"hooks\": [{\"command\": \"x\"}, {\"command\": \"y\"}]}]}");

        merger.RemoveElement(["groups", "0", "hooks"], 0);

        Assert.AreEqual("{\"groups\": [{\"hooks\": [{\"command\": \"y\"}]}]}", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ByteOrderMarkIsKeptAndNewlinesFollowTheFile()
    {
        var merger = new JsoncMerger("\uFEFF{\r\n  \"a\": 1\r\n}");

        merger.AddMember([], "b", 2);

        Assert.AreEqual("\uFEFF{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}", merger.Text);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Constructor_RejectsInvalidJson()
    {
        Assert.Throws<JsonException>(() => new JsoncMerger("{ \"a\": 1, "));
        Assert.IsNull(JsoncMerger.ParseObject("{ \"a\": 1, "));
        Assert.IsNull(JsoncMerger.ParseObject("[1, 2]"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ParseObject_AcceptsCommentsAndTrailingCommas()
    {
        var parsed = JsoncMerger.ParseObject("// header\n{ \"a\": [1, 2,], /* x */ \"b\": true, }");

        Assert.IsNotNull(parsed);
        Assert.IsTrue(parsed["b"]!.GetValue<bool>());
    }
}
