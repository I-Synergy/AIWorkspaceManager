namespace AiWorkspaceManager.Common;

/// <summary>Values the resolver and the extension agree on.</summary>
public static class ResolverDefaults
{
    /// <summary>Resolver version reported by the CLI and the handshake.</summary>
    public const string Version = "0.1.0";

    public const string TemplateSource = "template";
    public const string TemplateRepo = "I-Synergy/AI";
    public const string LocalRef = "local";
    public const string License = "MIT";

    /// <summary>Item kinds that declare dependencies, and the manifest list they live in.</summary>
    public const string SkillKind = "skill";
    public const string AgentKind = "agent";

    /// <summary>Manifest key that removes items a preset or source would otherwise bring in.</summary>
    public const string ExcludeKey = "exclude";

    /// <summary>Planned sync steps, as written to <c>SyncAction.Action</c>.</summary>
    public const string ActionCreate = "create";
    public const string ActionUpdate = "update";
    public const string ActionConflict = "conflict";
    public const string ActionOk = "ok";
    public const string ActionNoOp = "no-op";

    /// <summary>Every item kind, so payloads and folder walks can name them without a literal.</summary>
    public const string InstructionKind = "instruction";
    public const string PromptKind = "prompt";
    public const string PluginKind = "plugin";
    public const string HookKind = "hook";

    /// <summary>Item kinds, in the order every payload and folder walk uses.</summary>
    public static readonly IReadOnlyList<string> Kinds =
    [
        SkillKind,
        AgentKind,
        InstructionKind,
        PromptKind,
        PluginKind,
        HookKind,
    ];

    /// <summary>Manifest array that holds each kind's selection.</summary>
    public static readonly IReadOnlyDictionary<string, string> ManifestKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SkillKind] = "skills",
            [AgentKind] = "agents",
            [InstructionKind] = "instructions",
            [PromptKind] = "prompts",
            [PluginKind] = "plugins",
            [HookKind] = "hooks",
        };

    /// <summary>Folder name for each kind inside a source and under <c>.ai/</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> KindDirectories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SkillKind] = "skills",
            [AgentKind] = "agents",
            [InstructionKind] = "instructions",
            [PromptKind] = "prompts",
            [PluginKind] = "plugins",
            [HookKind] = "hooks",
        };

    /// <summary>How an installed item is named on disk, per kind.</summary>
    public static readonly IReadOnlyDictionary<string, string> KindSuffixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SkillKind] = "",
            [AgentKind] = ".md",
            [InstructionKind] = ".instructions.md",
            [PromptKind] = ".prompt.md",
            [PluginKind] = "",
            [HookKind] = "",
        };

    /// <summary>AI clients whose platform folders are materialised.</summary>
    public static readonly IReadOnlyList<string> Clients = ["claude", "copilot", "agents"];
}
