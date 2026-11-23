// File: tests/AiWorkspaceManager.Core.Workspace.Tests/SelectionEngineTests.cs
// Traces: none — legacy mode, this slice declares no acceptance criteria.
//
// Healing, ambiguity, closure order and exclusion semantics. These are the rules that decide what
// an install actually materialises, so they are pinned independently of the JSON payloads.

using System.Text.Json.Nodes;
using AiWorkspaceManager.Common;
using AiWorkspaceManager.Core.Catalog;
using AiWorkspaceManager.Core.Workspace;

namespace AiWorkspaceManager.Core.Workspace.Tests;

[TestClass]
public sealed class SelectionEngineTests
{
    private static SelectionEngine Engine(params (string Kind, string Id)[] items)
    {
        var index = items.ToDictionary(
            pair => pair,
            pair => new CatalogItem
            {
                Id = pair.Id,
                Source = ItemId.Split(pair.Id).Source,
                Kind = pair.Kind,
                Name = ItemId.Split(pair.Id).Name,
                Path = $".ai/{pair.Kind}s/{ItemId.Split(pair.Id).Name}",
            });
        return new SelectionEngine(index, PresetCatalog.Empty);
    }

    private static ManifestDocument Manifest(string json) =>
        ManifestDocument.Temporary(JsonNode.Parse(json)!.AsObject());

    [TestMethod]
    [TestCategory("Unit")]
    public void Selection_ABareThirdPartyIdResolvesToTheOnlySourceOfferingIt()
    {
        var engine = Engine(("skill", "template:alpha"), ("skill", "labs:beta"));
        var manifest = Manifest("{\"skills\":[\"alpha\",\"beta\"]}");

        var selected = engine.Selection(manifest);

        CollectionAssert.AreEqual(
            new[] { ("skill", "template:alpha"), ("skill", "labs:beta") },
            selected.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Selection_ABareIdTheTemplateOffersStaysWithTheTemplate()
    {
        var engine = Engine(("skill", "template:beta"), ("skill", "labs:beta"));

        var selected = engine.Selection(Manifest("{\"skills\":[\"beta\"]}"));

        CollectionAssert.AreEqual(new[] { ("skill", "template:beta") }, selected.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Selection_ABareIdOfferedByTwoOtherSourcesFails()
    {
        var engine = Engine(("skill", "labs:beta"), ("skill", "other:beta"));

        Assert.ThrowsExactly<ResolverException>(
            () => engine.Selection(Manifest("{\"skills\":[\"beta\"]}")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Selection_ABareExclusionStillMatchesTheItemItNamed()
    {
        var engine = Engine(("skill", "template:alpha"), ("skill", "labs:beta"));

        var selected = engine.Selection(Manifest("{\"skills\":[\"alpha\",\"beta\"],\"exclude\":[\"beta\"]}"));

        CollectionAssert.AreEqual(new[] { ("skill", "template:alpha") }, selected.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LockedIds_ABareLockFreezesTheItemItNamed()
    {
        var engine = Engine(("skill", "template:alpha"), ("skill", "labs:beta"));

        var frozen = engine.LockedIds(Manifest("{\"locked\":[\"beta\"]}"));

        CollectionAssert.AreEquivalent(new[] { "labs:beta" }, frozen.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Closure_PutsDependenciesBeforeTheirDependent()
    {
        var engine = Engine(("agent", "template:lead"), ("skill", "template:alpha"), ("skill", "template:beta"));

        var resolved = engine.Closure([("agent", "template:lead")]);

        // The fixture index has no requires, so this is the plain order.
        CollectionAssert.AreEqual(new[] { ("agent", "template:lead") }, resolved.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Closure_UnknownDependencyFailsWithTheRequiringItem()
    {
        var index = new Dictionary<(string, string), CatalogItem>
        {
            [("agent", "template:lead")] = new()
            {
                Id = "template:lead",
                Source = "template",
                Kind = "agent",
                Name = "lead",
                Path = ".ai/agents/lead.md",
                Requires = ["template:missing"],
            },
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        var error = Assert.ThrowsExactly<ResolverException>(
            () => engine.Closure([("agent", "template:lead")]));

        StringAssert.Contains(error.Message, "missing");
        StringAssert.Contains(error.Message, "lead");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Clients_MissingFieldKeepsEveryLegacyClient()
    {
        var clients = SelectionEngine.Clients(Manifest("{}"));

        CollectionAssert.AreEquivalent(new[] { "claude", "copilot", "agents" }, clients.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Clients_UnknownNameFails()
    {
        Assert.ThrowsExactly<ResolverException>(
            () => SelectionEngine.Clients(Manifest("{\"clients\":[\"claude\",\"cursor\"]}")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void WithDependencies_TagsEachPulledInItemWithTheItemThatRequiresIt()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:alpha")] = Item("skill", "template:alpha"),
            [("skill", "template:beta")] = Item("skill", "template:beta", "template:alpha"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        var entries = engine.WithDependencies([("skill", "template:beta")]);

        Assert.AreEqual(2, entries.Count);
        Assert.IsNull(entries[0].RequiredBy);
        Assert.AreEqual("template:alpha", entries[1].Id);
        Assert.AreEqual("template:beta", entries[1].RequiredBy);
    }

    /// <summary>
    /// The catalog stores <c>requires:</c> as full ids. A dependency that resolves must be tagged
    /// on the entry that pulled it in — reading the tag off a tuple's element name instead of the
    /// entry silently yields the null of the root, and every dependency then looks pre-selected.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void WithDependencies_TagsADependencyWhoseIdIsAlreadyCanonical()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:review-solution")] =
                Item("skill", "template:review-solution", "template:verify-config", "template:gap-review"),
            [("skill", "template:verify-config")] = Item("skill", "template:verify-config"),
            [("skill", "template:gap-review")] = Item("skill", "template:gap-review", "template:code-reviewer"),
            [("skill", "template:code-reviewer")] = Item("skill", "template:code-reviewer"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        var entries = engine.WithDependencies([("skill", "template:review-solution")]);

        Assert.AreEqual(
            "template:review-solution<null>,template:verify-config<template:review-solution>,"
            + "template:gap-review<template:review-solution>,template:code-reviewer<template:gap-review>",
            string.Join(",", entries.Select(entry => $"{entry.Id}<{entry.RequiredBy ?? "null"}>")));
    }

    /// <summary>
    /// An agent hands work to another agent, and the catalog stores that hand-off as the agent's
    /// full id. The closure has to follow it into the agent, not guess a skill of the same name.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void WithDependencies_FollowsAnAgentHandOffIntoAnotherAgent()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("agent", "template:architect")] = Item("agent", "template:architect", "template:developer"),
            [("agent", "template:developer")] = Item("agent", "template:developer", "template:dotnet-engineer"),
            [("skill", "template:dotnet-engineer")] = Item("skill", "template:dotnet-engineer"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        var entries = engine.WithDependencies([("agent", "template:architect")]);

        Assert.AreEqual(
            "agent/template:architect<null>,agent/template:developer<template:architect>,"
            + "skill/template:dotnet-engineer<template:developer>",
            string.Join(",", entries.Select(entry => $"{entry.Kind}/{entry.Id}<{entry.RequiredBy ?? "null"}>")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DependencyKey_ResolvesABareAgentNameWhenNoSkillCarriesIt()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("agent", "template:developer")] = Item("agent", "template:developer"),
            [("skill", "template:code-reviewer")] = Item("skill", "template:code-reviewer"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        Assert.AreEqual(("agent", "template:developer"), engine.DependencyKey("developer"));
        Assert.AreEqual(("skill", "template:code-reviewer"), engine.DependencyKey("code-reviewer"));
        Assert.IsNull(engine.DependencyKey("does-not-exist"));
    }

    /// <summary>A skill wins a bare name that both kinds carry, so existing catalogs keep resolving.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void DependencyKey_PrefersTheSkillWhenBothKindsCarryTheName()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:security")] = Item("skill", "template:security"),
            [("agent", "template:security")] = Item("agent", "template:security"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        Assert.AreEqual(("skill", "template:security"), engine.DependencyKey("security"));
    }

    /// <summary>
    /// `architect` and `security` are both a skill and an agent, so a bare name cannot say which
    /// one a catalog means. The kind-qualified form states it outright.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void DependencyKey_KindPrefixPicksTheKindWhenBothCarryTheName()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:architect")] = Item("skill", "template:architect"),
            [("agent", "template:architect")] = Item("agent", "template:architect"),
            [("skill", "template:security")] = Item("skill", "template:security"),
            [("agent", "template:security")] = Item("agent", "template:security"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        Assert.AreEqual(("agent", "template:architect"), engine.DependencyKey("agent:architect"));
        Assert.AreEqual(("skill", "template:architect"), engine.DependencyKey("skill:architect"));
        Assert.AreEqual(("agent", "template:security"), engine.DependencyKey("agent:security"));
        Assert.AreEqual(("skill", "template:security"), engine.DependencyKey("skill:security"));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DependencyKey_KindPrefixThatMatchesNothingIsUnresolved()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:code-reviewer")] = Item("skill", "template:code-reviewer"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        Assert.IsNull(engine.DependencyKey("agent:code-reviewer"));
        Assert.AreEqual(("skill", "template:code-reviewer"), engine.DependencyKey("skill:code-reviewer"));
    }

    /// <summary>
    /// A stored full id that both kinds carry (`template:security`) must resolve the same way the
    /// bare name does — to the skill — or a catalog that stores ids fails to find a skill that is
    /// plainly in the index.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void DependencyKey_ResolvesAFullIdBothKindsCarryToTheSkill()
    {
        var index = new Dictionary<(string Kind, string Id), CatalogItem>
        {
            [("skill", "template:security")] = Item("skill", "template:security"),
            [("agent", "template:security")] = Item("agent", "template:security"),
        };
        var engine = new SelectionEngine(index, PresetCatalog.Empty);

        Assert.AreEqual(("skill", "template:security"), engine.DependencyKey("template:security"));
    }

    private static CatalogItem Item(string kind, string id, params string[] requires) => new()
    {
        Id = id,
        Source = ItemId.Split(id).Source,
        Kind = kind,
        Name = ItemId.Split(id).Name,
        Path = $".ai/{kind}s/{ItemId.Split(id).Name}",
        Requires = requires,
    };
}
