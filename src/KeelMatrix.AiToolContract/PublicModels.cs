using System.Collections.ObjectModel;

namespace KeelMatrix.AiToolContract;

/// <summary>Compatibility severity assigned to a contract difference.</summary>
public enum AiToolCompatibility
{
    /// <summary>No compatibility concern.</summary>
    Informational = 0,
    /// <summary>Compatible additive change.</summary>
    Additive = 1,
    /// <summary>Change requiring review.</summary>
    Risky = 2,
    /// <summary>Change that can break a consumer.</summary>
    Breaking = 3
}

/// <summary>Kind of contract difference found between two baselines.</summary>
public enum AiToolChangeKind
{
    /// <summary>A tool was added.</summary>
    ToolAdded,
    /// <summary>A tool was removed.</summary>
    ToolRemoved,
    /// <summary>A required parameter was added.</summary>
    RequiredParameterAdded,
    /// <summary>A parameter was removed.</summary>
    ParameterRemoved,
    /// <summary>A parameter type changed.</summary>
    TypeChanged,
    /// <summary>An enum value was removed.</summary>
    EnumNarrowed,
    /// <summary>An enum value was added.</summary>
    EnumExpanded,
    /// <summary>A schema constraint was narrowed.</summary>
    ConstraintNarrowed,
    /// <summary>A schema constraint was expanded.</summary>
    ConstraintExpanded,
    /// <summary>An optional parameter was added.</summary>
    OptionalParameterAdded,
    /// <summary>A return schema became more restrictive.</summary>
    ReturnSchemaBreaking,
    /// <summary>A return schema became less restrictive.</summary>
    ReturnSchemaAdditive,
    /// <summary>A model-facing description changed.</summary>
    DescriptionChanged,
    /// <summary>Approval or safety metadata changed.</summary>
    ApprovalSafetyMetadataChanged,
    /// <summary>A change could not be classified safely.</summary>
    Unsupported
}

/// <summary>Diagnostic category for a capture, baseline, resource, or comparison result.</summary>
public enum AiToolDiagnosticKind
{
    /// <summary>Capture of framework metadata failed.</summary>
    CaptureFailure,
    /// <summary>Two tools have the same identity.</summary>
    DuplicateToolIdentity,
    /// <summary>The baseline is malformed.</summary>
    MalformedBaseline,
    /// <summary>The baseline version is not supported.</summary>
    UnsupportedBaselineVersion,
    /// <summary>A canonicalization or resource limit was exceeded.</summary>
    CanonicalizationOrResourceLimit,
    /// <summary>A compatibility difference was found.</summary>
    CompatibilityDifference,
    /// <summary>A difference could not be classified safely.</summary>
    UnsupportedClassification
}

/// <summary>Limits that keep capture, parsing, and comparison bounded.</summary>
public sealed class AiToolContractLimits
{
    /// <summary>Default safe limits.</summary>
    public static AiToolContractLimits Default => new AiToolContractLimits();

    /// <summary>Maximum number of tools in a catalog.</summary>
    public int MaxTools { get; set; } = 256;

    /// <summary>Maximum UTF-8 bytes in one schema.</summary>
    public int MaxSchemaBytes { get; set; } = 1_048_576;

    /// <summary>Maximum schema nesting depth.</summary>
    public int MaxSchemaDepth { get; set; } = 64;

    /// <summary>Maximum object properties in one schema.</summary>
    public int MaxProperties { get; set; } = 10_000;

    /// <summary>Maximum array items in one schema array.</summary>
    public int MaxArrayItems { get; set; } = 10_000;

    /// <summary>Maximum reported changes in one comparison.</summary>
    public int MaxChanges { get; set; } = 5_000;

    internal void Validate()
    {
        if (MaxTools < 1 || MaxSchemaBytes < 1 || MaxSchemaDepth < 1 || MaxProperties < 1 || MaxArrayItems < 1 || MaxChanges < 1)
            throw new ArgumentOutOfRangeException(nameof(AiToolContractLimits), "All contract limits must be positive.");
    }

    internal int GetJsonDocumentMaxDepth(int envelopeDepth)
    {
        var requested = (long)MaxSchemaDepth + envelopeDepth;
        return (int)Math.Min(1000L, requested);
    }
}

/// <summary>A diagnostic that explains why a result is not clean.</summary>
public sealed class AiToolContractDiagnostic
{
    /// <summary>Creates a diagnostic.</summary>
    public AiToolContractDiagnostic(AiToolDiagnosticKind kind, string message)
    {
        Kind = kind;
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>Diagnostic category.</summary>
    public AiToolDiagnosticKind Kind { get; }

    /// <summary>Actionable diagnostic text.</summary>
    public string Message { get; }
}

/// <summary>A captured, versioned tool contract entry.</summary>
public sealed class AiToolContractTool
{
    internal AiToolContractTool(string name, string? description, string inputSchemaJson, string? returnSchemaJson, bool requiresApproval)
        : this(
            name,
            description,
            SchemaNormalizer.Normalize(inputSchemaJson, AiToolContractLimits.Default),
            returnSchemaJson is null ? null : SchemaNormalizer.Normalize(returnSchemaJson, AiToolContractLimits.Default),
            requiresApproval)
    {
    }

    internal AiToolContractTool(string name, string? description, NormalizedSchema inputSchema, NormalizedSchema? returnSchema, bool requiresApproval)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description;
        InputSchema = inputSchema ?? throw new ArgumentNullException(nameof(inputSchema));
        ReturnSchema = returnSchema;
        InputSchemaJson = inputSchema.ToCanonicalJson();
        ReturnSchemaJson = returnSchema?.ToCanonicalJson();
        RequiresApproval = requiresApproval;
    }

    internal void ValidateLimits(AiToolContractLimits limits)
    {
        InputSchema.ValidateLimits(limits);
        ReturnSchema?.ValidateLimits(limits);
    }

    /// <summary>Tool identity using ordinal comparison.</summary>
    public string Name { get; }

    /// <summary>Model-facing description, if supplied.</summary>
    public string? Description { get; }

    /// <summary>Canonical input JSON Schema.</summary>
    public string InputSchemaJson { get; }

    internal NormalizedSchema InputSchema { get; }

    /// <summary>Canonical return JSON Schema, if supplied.</summary>
    public string? ReturnSchemaJson { get; }

    internal NormalizedSchema? ReturnSchema { get; }

    /// <summary>Whether the current framework abstraction explicitly requires approval.</summary>
    public bool RequiresApproval { get; }
}

/// <summary>A deterministic KeelMatrix-owned tool catalog baseline.</summary>
public sealed class AiToolContractBaseline
{
    internal AiToolContractBaseline(IReadOnlyList<AiToolContractTool> tools)
    {
        SchemaVersion = 1;
        Tools = new ReadOnlyCollection<AiToolContractTool>(tools.ToList());
    }

    /// <summary>Current KeelMatrix baseline schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Tools sorted by ordinal name.</summary>
    public IReadOnlyList<AiToolContractTool> Tools { get; }

    internal void ValidateLimits(AiToolContractLimits limits)
    {
        limits.Validate();
        if (Tools.Count > limits.MaxTools)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The baseline exceeds the configured tool-count limit."));

        foreach (var tool in Tools)
            tool.ValidateLimits(limits);
    }
}

/// <summary>Result of capturing framework tool metadata.</summary>
public sealed class AiToolContractCaptureResult
{
    internal AiToolContractCaptureResult(AiToolContractBaseline? baseline, AiToolContractDiagnostic? diagnostic)
    {
        Baseline = baseline;
        Diagnostic = diagnostic;
    }

    /// <summary>Whether capture succeeded.</summary>
    public bool Succeeded => Baseline is not null && Diagnostic is null;

    /// <summary>Captured baseline when successful.</summary>
    public AiToolContractBaseline? Baseline { get; }

    /// <summary>Failure diagnostic when capture failed.</summary>
    public AiToolContractDiagnostic? Diagnostic { get; }
}

/// <summary>One classified contract change.</summary>
public sealed class AiToolChange
{
    internal AiToolChange(AiToolChangeKind kind, AiToolCompatibility compatibility, string toolName, string path, string message)
    {
        Kind = kind;
        Compatibility = compatibility;
        ToolName = toolName;
        Path = path;
        Message = message;
    }

    /// <summary>Change category.</summary>
    public AiToolChangeKind Kind { get; }

    /// <summary>Compatibility severity.</summary>
    public AiToolCompatibility Compatibility { get; }

    /// <summary>Affected tool name.</summary>
    public string ToolName { get; }

    /// <summary>Schema or metadata path, when known.</summary>
    public string Path { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }
}

/// <summary>Structured comparison of two tool contract baselines.</summary>
public sealed class AiToolContractDiff
{
    internal AiToolContractDiff(IReadOnlyList<AiToolChange> changes, IReadOnlyList<AiToolContractDiagnostic> diagnostics)
    {
        Changes = new ReadOnlyCollection<AiToolChange>(changes.ToList());
        Diagnostics = new ReadOnlyCollection<AiToolContractDiagnostic>(diagnostics.ToList());
        Compatibility = changes.Count == 0 ? AiToolCompatibility.Informational : changes.Max(static c => c.Compatibility);
    }

    /// <summary>True only when no changes or diagnostics require review.</summary>
    public bool IsClean => Changes.Count == 0 && Diagnostics.Count == 0;

    /// <summary>Highest compatibility severity in the diff.</summary>
    public AiToolCompatibility Compatibility { get; }

    /// <summary>Classified differences.</summary>
    public IReadOnlyList<AiToolChange> Changes { get; }

    /// <summary>Comparison diagnostics.</summary>
    public IReadOnlyList<AiToolContractDiagnostic> Diagnostics { get; }

    internal bool HasBreaking => Changes.Any(static c => c.Compatibility == AiToolCompatibility.Breaking);
    internal bool HasUnsupported => Diagnostics.Any(static d => d.Kind == AiToolDiagnosticKind.UnsupportedClassification) || Changes.Any(static c => c.Kind == AiToolChangeKind.Unsupported);
}

/// <summary>Result of capturing and verifying a live tool catalog.</summary>
public sealed class AiToolContractVerificationResult
{
    internal AiToolContractVerificationResult(AiToolContractCaptureResult capture, AiToolContractDiff? diff)
    {
        Capture = capture;
        Diff = diff;
    }

    /// <summary>Whether the live catalog matched the baseline.</summary>
    public bool IsClean => Diff?.IsClean == true;

    /// <summary>Capture result for the live catalog.</summary>
    public AiToolContractCaptureResult Capture { get; }

    /// <summary>Diff when capture succeeded.</summary>
    public AiToolContractDiff? Diff { get; }
}

/// <summary>Exception for fail-closed baseline and contract operations.</summary>
public sealed class AiToolContractException : Exception
{
    /// <summary>Creates a contract exception.</summary>
    public AiToolContractException(AiToolContractDiagnostic diagnostic)
        : base(diagnostic?.Message ?? throw new ArgumentNullException(nameof(diagnostic)))
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Diagnostic represented by this exception.</summary>
    public AiToolContractDiagnostic Diagnostic { get; }
}
