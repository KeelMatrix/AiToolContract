using Microsoft.Extensions.AI;

namespace KeelMatrix.AiToolContract;

/// <summary>Compares captured baselines and requires explicit acceptance for updates.</summary>
public static class AiToolContractVerifier
{
    /// <summary>Compares two baselines without writing either one.</summary>
    public static AiToolContractDiff Compare(AiToolContractBaseline baseline, AiToolContractBaseline candidate, AiToolContractLimits? limits = null)
    {
        if (baseline is null)
            throw new ArgumentNullException(nameof(baseline));
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        var effectiveLimits = limits ?? AiToolContractLimits.Default;
        effectiveLimits.Validate();
        if (baseline.SchemaVersion != 1 || candidate.SchemaVersion != 1)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedBaselineVersion, "Only baseline schema version 1 is supported."));
        AiToolContractJson.ValidateBaselineLimits(baseline, effectiveLimits);
        AiToolContractJson.ValidateBaselineLimits(candidate, effectiveLimits);

        var changes = new List<AiToolChange>();
        var diagnostics = new List<AiToolContractDiagnostic>();
        var unsupportedKeys = new HashSet<UnsupportedSemanticKey>();
        foreach (var failure in SchemaTransitionRules.CoverageFailures)
            AddUnsupported(changes, diagnostics, effectiveLimits, unsupportedKeys, "$", "$.normalizedModel", "The normalized schema model is not fully covered by explicit transition rules: " + failure, null);

        var oldTools = baseline.Tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var newTools = candidate.Tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);

        foreach (var oldTool in oldTools.Values)
        {
            if (!newTools.ContainsKey(oldTool.Name))
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ToolRemoved, AiToolCompatibility.Breaking, oldTool.Name, "$", "Tool '" + oldTool.Name + "' was removed."));
        }

        foreach (var newTool in newTools.Values)
        {
            if (!oldTools.ContainsKey(newTool.Name))
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ToolAdded, AiToolCompatibility.Additive, newTool.Name, "$", "Tool '" + newTool.Name + "' was added."));
        }

        foreach (var oldTool in oldTools.Values)
        {
            if (!newTools.TryGetValue(oldTool.Name, out var newTool))
                continue;

            if (!string.Equals(oldTool.Description, newTool.Description, StringComparison.Ordinal))
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky, oldTool.Name, "$.description", "Tool description changed; model selection behavior may change."));

            if (oldTool.RequiresApproval != newTool.RequiresApproval)
            {
                var compatibility = newTool.RequiresApproval ? AiToolCompatibility.Risky : AiToolCompatibility.Breaking;
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ApprovalSafetyMetadataChanged, compatibility, oldTool.Name, "$.requiresApproval", "Approval/safety metadata changed and requires explicit review."));
            }

            CompareSchema(oldTool.InputSchema, newTool.InputSchema, oldTool.Name, "$", true, changes, diagnostics, effectiveLimits, unsupportedKeys, CreateRootNode(true));

            if (oldTool.ReturnSchema is null && newTool.ReturnSchema is not null)
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, oldTool.Name, "$.returnSchema", "A return schema was added."));
            else if (oldTool.ReturnSchema is not null && newTool.ReturnSchema is null)
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, oldTool.Name, "$.returnSchema", "The return schema was removed."));
            else if (oldTool.ReturnSchema is not null && newTool.ReturnSchema is not null)
                CompareSchema(oldTool.ReturnSchema, newTool.ReturnSchema, oldTool.Name, "$.returnSchema", false, changes, diagnostics, effectiveLimits, unsupportedKeys, CreateRootNode(false));
        }

        if (changes.Count > 0)
            diagnostics.Add(new AiToolContractDiagnostic(AiToolDiagnosticKind.CompatibilityDifference, "The candidate differs from the baseline; no baseline was updated automatically."));
        return new AiToolContractDiff(changes, diagnostics);
    }

    /// <summary>Captures live tools and compares them with a baseline.</summary>
    public static AiToolContractVerificationResult Verify(AiToolContractBaseline baseline, IEnumerable<AITool> tools, AiToolContractLimits? limits = null)
    {
        if (baseline is null)
            throw new ArgumentNullException(nameof(baseline));
        var capture = AiToolContractCapture.Capture(tools ?? throw new ArgumentNullException(nameof(tools)), limits);
        return new AiToolContractVerificationResult(capture, capture.Succeeded ? Compare(baseline, capture.Baseline!, limits) : null);
    }

    /// <summary>
    /// Explicitly accepts a candidate only when it has no breaking or unsupported differences.
    /// This method never writes to disk.
    /// </summary>
    public static AiToolContractBaseline Accept(AiToolContractBaseline candidate, AiToolContractDiff diff)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (diff is null)
            throw new ArgumentNullException(nameof(diff));
        if (diff.HasUnsupported)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "The candidate contains an unsupported difference and cannot be accepted automatically."));
        if (diff.HasBreaking)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CompatibilityDifference, "Breaking changes require an explicit breaking-change review."));
        return candidate;
    }

    /// <summary>Explicitly accepts a candidate after the caller has completed breaking-change review.</summary>
    public static AiToolContractBaseline AcceptWithBreakingReview(AiToolContractBaseline candidate, AiToolContractDiff diff)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));
        if (diff is null)
            throw new ArgumentNullException(nameof(diff));
        if (diff.HasUnsupported)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "The candidate contains an unsupported difference and cannot be accepted."));
        return candidate;
    }

    private static void CompareSchema(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        var transitions = SchemaTransitionRules.FindTransitions(oldSchema, newSchema);
        foreach (var transition in transitions)
        {
            if (!SchemaTransitionRules.TryGetRule(transition.PropertyName, transition.Direction, out var rule) || (input ? rule!.Input : rule!.Return).Unsupported)
                AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, AppendModelPropertyPath(path, transition.PropertyName), "The normalized schema transition '" + transition.PropertyName + ":" + transition.Direction + "' has no explicit compatibility rule.", CreateUnsupportedKey(toolName, nodeIdentity, transition.PropertyName, transition.Direction));
        }

        if (oldSchema.SemanticallyEquals(newSchema))
            return;

        // A reference is validated without dereferencing. Any target or sibling
        // change remains review-only so comparison cannot invent reference semantics.
        if (oldSchema.Reference is not null || newSchema.Reference is not null)
        {
            AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, path + ".$ref", "Reference target or sibling schema changed and requires review.", CreateUnsupportedKey(toolName, nodeIdentity, nameof(NormalizedSchema.Reference), FindDirection(transitions, nameof(NormalizedSchema.Reference))));
            return;
        }

        CompareDescription(oldSchema, newSchema, toolName, path, changes, limits);
        CompareDefault(oldSchema, newSchema, toolName, path, transitions, changes, diagnostics, limits, unsupportedKeys, nodeIdentity);
        CompareFormat(oldSchema, newSchema, toolName, path, transitions, changes, diagnostics, limits, unsupportedKeys, nodeIdentity);
        CompareType(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareEnum(oldSchema, newSchema, toolName, path, input, transitions, changes, diagnostics, limits, unsupportedKeys, nodeIdentity);
        CompareConstraints(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareObjectProperties(oldSchema, newSchema, toolName, path, input, changes, diagnostics, limits, unsupportedKeys, nodeIdentity);
        CompareRequired(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareItems(oldSchema, newSchema, toolName, path, input, transitions, changes, diagnostics, limits, unsupportedKeys, nodeIdentity);
    }

    private static void CompareDescription(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        if (!string.Equals(oldSchema.Description, newSchema.Description, StringComparison.Ordinal))
            Add(changes, limits, new AiToolChange(AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky, toolName, path + ".description", "Schema description changed; model behavior may change."));
    }

    private static void CompareDefault(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, IReadOnlyList<SchemaTransition> transitions, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        if (oldSchema.HasDefault == newSchema.HasDefault && (!oldSchema.HasDefault || oldSchema.DefaultValue!.SemanticallyEquals(newSchema.DefaultValue!)))
            return;
        AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, path + ".default", "Default value changed; comparison is review-only for this schema metadata.", CreateUnsupportedKey(toolName, nodeIdentity, nameof(NormalizedSchema.DefaultValue), FindDirection(transitions, nameof(NormalizedSchema.HasDefault), nameof(NormalizedSchema.DefaultValue))));
    }

    private static void CompareFormat(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, IReadOnlyList<SchemaTransition> transitions, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        if (string.Equals(oldSchema.Format, newSchema.Format, StringComparison.Ordinal))
            return;
        AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, path + ".format", "Format changed; comparison is review-only for this schema metadata.", CreateUnsupportedKey(toolName, nodeIdentity, nameof(NormalizedSchema.Format), FindDirection(transitions, nameof(NormalizedSchema.Format))));
    }

    private static void CompareObjectProperties(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        foreach (var oldProperty in oldSchema.Properties)
        {
            if (!newSchema.Properties.TryGetValue(oldProperty.Key, out var newProperty))
            {
                var kind = input ? AiToolChangeKind.ParameterRemoved : AiToolChangeKind.ReturnSchemaBreaking;
                Add(changes, limits, new AiToolChange(kind, AiToolCompatibility.Breaking, toolName, path + ".properties." + oldProperty.Key, "Property '" + oldProperty.Key + "' was removed."));
            }
            else
            {
                CompareSchema(oldProperty.Value, newProperty, toolName, path + ".properties." + oldProperty.Key, input, changes, diagnostics, limits, unsupportedKeys, nodeIdentity.Property(oldProperty.Key));
            }
        }

        foreach (var newProperty in newSchema.Properties)
        {
            if (oldSchema.Properties.ContainsKey(newProperty.Key))
                continue;
            var required = newSchema.Required.Contains(newProperty.Key, StringComparer.Ordinal);
            if (!input)
            {
                var kind = required ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
                var compatibility = required ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
                Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + ".properties." + newProperty.Key, "Return property '" + newProperty.Key + "' was added."));
            }
            else if (required)
            {
                Add(changes, limits, new AiToolChange(AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, toolName, path + ".properties." + newProperty.Key, "Required parameter '" + newProperty.Key + "' was added."));
            }
            else
            {
                Add(changes, limits, new AiToolChange(AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, toolName, path + ".properties." + newProperty.Key, "Optional parameter '" + newProperty.Key + "' was added."));
            }
        }
    }

    private static void CompareRequired(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        foreach (var name in newSchema.Required.Except(oldSchema.Required, StringComparer.Ordinal))
        {
            // A property addition/removal already reports the structural change.
            // Keep the required-set transition for declared properties that exist
            // on both sides and for valid undeclared required names.
            if (oldSchema.Properties.ContainsKey(name) == newSchema.Properties.ContainsKey(name))
                Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.RequiredParameterAdded : AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, toolName, path + ".required", "Property '" + name + "' became required."));
        }
        foreach (var name in oldSchema.Required.Except(newSchema.Required, StringComparer.Ordinal))
        {
            if (oldSchema.Properties.ContainsKey(name) == newSchema.Properties.ContainsKey(name))
            {
                var kind = input ? AiToolChangeKind.OptionalParameterAdded : AiToolChangeKind.ReturnSchemaAdditive;
                Add(changes, limits, new AiToolChange(kind, AiToolCompatibility.Additive, toolName, path + ".required", input ? "Parameter '" + name + "' is no longer required." : "Return property '" + name + "' is no longer required."));
            }
        }
    }

    private static void CompareItems(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, IReadOnlyList<SchemaTransition> transitions, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        if (oldSchema.Items is null && newSchema.Items is null)
            return;
        if (oldSchema.Items is null || newSchema.Items is null)
        {
            AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, path + ".items", "Array item schema presence changed and requires review.", CreateUnsupportedKey(toolName, nodeIdentity, nameof(NormalizedSchema.Items), FindDirection(transitions, nameof(NormalizedSchema.Items))));
            return;
        }
        CompareSchema(oldSchema.Items, newSchema.Items, toolName, path + ".items", input, changes, diagnostics, limits, unsupportedKeys, nodeIdentity.Items());
    }

    private static void CompareType(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        if (oldSchema.Types.SequenceEqual(newSchema.Types, StringComparer.Ordinal))
            return;

        var oldTypes = oldSchema.Types;
        var newTypes = newSchema.Types;
        var narrowed = oldTypes.Count == 0
            ? newTypes.Count > 0
            : newTypes.Count > 0 && IsSubset(newTypes, oldTypes) && !IsSubset(oldTypes, newTypes);
        var widened = newTypes.Count == 0
            ? oldTypes.Count > 0
            : oldTypes.Count > 0 && IsSubset(oldTypes, newTypes) && !IsSubset(newTypes, oldTypes);
        var compatibility = narrowed ? AiToolCompatibility.Breaking : widened ? AiToolCompatibility.Additive : AiToolCompatibility.Breaking;
        var kind = input
            ? AiToolChangeKind.TypeChanged
            : narrowed || !widened ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
        Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + ".type", "Schema type " + (narrowed ? "narrowed" : widened ? "widened" : "changed") + "."));
    }

    private static void CompareEnum(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, IReadOnlyList<SchemaTransition> transitions, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, SchemaNodeIdentity nodeIdentity)
    {
        if (oldSchema.EnumValues is null && newSchema.EnumValues is null)
            return;

        var narrowed = oldSchema.EnumValues is null || newSchema.EnumValues is not null && IsSubset(newSchema.EnumValues, oldSchema.EnumValues) && !IsSubset(oldSchema.EnumValues, newSchema.EnumValues);
        var widened = newSchema.EnumValues is null || oldSchema.EnumValues is not null && IsSubset(oldSchema.EnumValues, newSchema.EnumValues) && !IsSubset(newSchema.EnumValues, oldSchema.EnumValues);
        if (!narrowed && !widened)
        {
            if (ValuesEqual(oldSchema.EnumValues!, newSchema.EnumValues!))
                return;
            AddUnsupported(changes, diagnostics, limits, unsupportedKeys, toolName, path + ".enum", "Enum values changed in both directions and cannot be classified safely.", CreateUnsupportedKey(toolName, nodeIdentity, nameof(NormalizedSchema.EnumValues), FindDirection(transitions, nameof(NormalizedSchema.EnumValues))));
        }
        else if (narrowed)
            Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.EnumNarrowed : AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, toolName, path + ".enum", "Enum values were narrowed."));
        else
            Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.EnumExpanded : AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, toolName, path + ".enum", "Enum values were expanded."));
    }

    private static void CompareConstraints(NormalizedSchema oldSchema, NormalizedSchema newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        CompareBound(oldSchema.Minimum, newSchema.Minimum, "minimum", true, toolName, path, input, changes, limits);
        CompareBound(oldSchema.ExclusiveMinimum, newSchema.ExclusiveMinimum, "exclusiveMinimum", true, toolName, path, input, changes, limits);
        CompareBound(oldSchema.MinLength, newSchema.MinLength, "minLength", true, toolName, path, input, changes, limits);
        CompareBound(oldSchema.MinItems, newSchema.MinItems, "minItems", true, toolName, path, input, changes, limits);
        CompareBound(oldSchema.Maximum, newSchema.Maximum, "maximum", false, toolName, path, input, changes, limits);
        CompareBound(oldSchema.ExclusiveMaximum, newSchema.ExclusiveMaximum, "exclusiveMaximum", false, toolName, path, input, changes, limits);
        CompareBound(oldSchema.MaxLength, newSchema.MaxLength, "maxLength", false, toolName, path, input, changes, limits);
        CompareBound(oldSchema.MaxItems, newSchema.MaxItems, "maxItems", false, toolName, path, input, changes, limits);
    }

    private static void CompareBound(JsonNumber? oldValue, JsonNumber? newValue, string name, bool lowerBound, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        if (!oldValue.HasValue && !newValue.HasValue)
            return;
        var narrowed = false;
        var expanded = false;
        if (!oldValue.HasValue && newValue.HasValue)
            narrowed = true;
        else if (oldValue.HasValue && !newValue.HasValue)
            expanded = true;
        else
        {
            var comparison = newValue!.Value.CompareTo(oldValue!.Value);
            narrowed = lowerBound ? comparison > 0 : comparison < 0;
            expanded = lowerBound ? comparison < 0 : comparison > 0;
        }

        if (!narrowed && !expanded)
            return;
        var kind = narrowed ? AiToolChangeKind.ConstraintNarrowed : AiToolChangeKind.ConstraintExpanded;
        var compatibility = narrowed ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
        if (!input)
            kind = narrowed ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
        Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + "." + name, "Constraint changed."));
    }

    private static bool ValuesEqual(IReadOnlyList<NormalizedJsonValue> left, IReadOnlyList<NormalizedJsonValue> right) =>
        left.Count == right.Count && left.Zip(right, static (a, b) => a.SemanticallyEquals(b)).All(static equal => equal);

    private static bool IsSubset(IReadOnlyList<NormalizedJsonValue> subset, IReadOnlyList<NormalizedJsonValue> superset) =>
        subset.All(value => superset.Any(value.SemanticallyEquals));

    private static bool IsSubset(IReadOnlyList<string> subset, IReadOnlyList<string> superset) =>
        subset.All(value => superset.Contains(value, StringComparer.Ordinal));

    private static void AddUnsupported(List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits, HashSet<UnsupportedSemanticKey> unsupportedKeys, string toolName, string path, string message, UnsupportedSemanticKey? semanticKey)
    {
        if (semanticKey.HasValue && !unsupportedKeys.Add(semanticKey.Value))
            return;

        Add(changes, limits, new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path, message));
        diagnostics.Add(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, message));
    }

    private static void Add(List<AiToolChange> changes, AiToolContractLimits limits, AiToolChange change)
    {
        if (changes.Count >= limits.MaxChanges)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The comparison exceeds the configured change-count limit."));
        changes.Add(change);
    }

    private static SchemaNodeIdentity CreateRootNode(bool input) =>
        new(null, new SchemaNodeSegment(input ? SchemaNodeSegmentKind.Input : SchemaNodeSegmentKind.Return, null));

    private static SchemaTransitionDirection FindDirection(IReadOnlyList<SchemaTransition> transitions, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var transition = transitions.FirstOrDefault(candidate => string.Equals(candidate.PropertyName, propertyName, StringComparison.Ordinal));
            if (transition is not null)
                return transition.Direction;
        }
        return SchemaTransitionDirection.Changed;
    }

    private static UnsupportedSemanticKey CreateUnsupportedKey(string toolName, SchemaNodeIdentity nodeIdentity, string propertyName, SchemaTransitionDirection direction) =>
        new(toolName, nodeIdentity, CanonicalModelPropertyName(propertyName), direction);

    private static string AppendModelPropertyPath(string path, string propertyName) =>
        path + "." + CanonicalModelPropertyName(propertyName);

    private static string CanonicalModelPropertyName(string propertyName) => propertyName switch
    {
        nameof(NormalizedSchema.Reference) => "$ref",
        nameof(NormalizedSchema.Description) => "description",
        nameof(NormalizedSchema.HasDefault) or nameof(NormalizedSchema.DefaultValue) => "default",
        nameof(NormalizedSchema.Format) => "format",
        nameof(NormalizedSchema.Types) => "type",
        nameof(NormalizedSchema.Properties) => "properties",
        nameof(NormalizedSchema.Required) => "required",
        nameof(NormalizedSchema.EnumValues) => "enum",
        nameof(NormalizedSchema.Items) => "items",
        _ => char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1)
    };

    private enum SchemaNodeSegmentKind
    {
        Input,
        Return,
        Property,
        Items
    }

    private readonly struct SchemaNodeSegment : IEquatable<SchemaNodeSegment>
    {
        internal SchemaNodeSegment(SchemaNodeSegmentKind kind, string? value)
        {
            Kind = kind;
            Value = value;
        }

        private SchemaNodeSegmentKind Kind { get; }
        private string? Value { get; }

        public bool Equals(SchemaNodeSegment other) => Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is SchemaNodeSegment other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Kind * 397) ^ (Value?.GetHashCode() ?? 0);
            }
        }
    }

    private sealed class SchemaNodeIdentity : IEquatable<SchemaNodeIdentity>
    {
        internal SchemaNodeIdentity(SchemaNodeIdentity? parent, SchemaNodeSegment segment)
        {
            Parent = parent;
            Segment = segment;
        }

        private SchemaNodeIdentity? Parent { get; }
        private SchemaNodeSegment Segment { get; }

        internal SchemaNodeIdentity Property(string name) => new(this, new SchemaNodeSegment(SchemaNodeSegmentKind.Property, name));

        internal SchemaNodeIdentity Items() => new(this, new SchemaNodeSegment(SchemaNodeSegmentKind.Items, null));

        public bool Equals(SchemaNodeIdentity? other) =>
            other is not null && Segment.Equals(other.Segment) && (Parent is null ? other.Parent is null : Parent.Equals(other.Parent));

        public override bool Equals(object? obj) => obj is SchemaNodeIdentity other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Parent?.GetHashCode() ?? 0) * 397) ^ Segment.GetHashCode();
            }
        }
    }

    private readonly struct UnsupportedSemanticKey : IEquatable<UnsupportedSemanticKey>
    {
        internal UnsupportedSemanticKey(string toolName, SchemaNodeIdentity nodeIdentity, string modelProperty, SchemaTransitionDirection direction)
        {
            ToolName = toolName;
            NodeIdentity = nodeIdentity;
            ModelProperty = modelProperty;
            Direction = direction;
        }

        private string ToolName { get; }
        private SchemaNodeIdentity NodeIdentity { get; }
        private string ModelProperty { get; }
        private SchemaTransitionDirection Direction { get; }

        public bool Equals(UnsupportedSemanticKey other) =>
            string.Equals(ToolName, other.ToolName, StringComparison.Ordinal) &&
            NodeIdentity.Equals(other.NodeIdentity) &&
            string.Equals(ModelProperty, other.ModelProperty, StringComparison.Ordinal) &&
            Direction == other.Direction;

        public override bool Equals(object? obj) => obj is UnsupportedSemanticKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = ToolName.GetHashCode();
                hash = (hash * 397) ^ NodeIdentity.GetHashCode();
                hash = (hash * 397) ^ ModelProperty.GetHashCode();
                return (hash * 397) ^ (int)Direction;
            }
        }
    }
}
