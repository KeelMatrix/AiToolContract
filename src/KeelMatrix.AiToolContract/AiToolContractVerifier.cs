using System.Text.Json;
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

        var changes = new List<AiToolChange>();
        var diagnostics = new List<AiToolContractDiagnostic>();
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

            using var oldInput = JsonDocument.Parse(oldTool.InputSchemaJson);
            using var newInput = JsonDocument.Parse(newTool.InputSchemaJson);
            CompareSchema(oldInput.RootElement, newInput.RootElement, oldTool.Name, "$", true, changes, diagnostics, effectiveLimits);

            if (oldTool.ReturnSchemaJson is null && newTool.ReturnSchemaJson is not null)
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, oldTool.Name, "$.returnSchema", "A return schema was added."));
            else if (oldTool.ReturnSchemaJson is not null && newTool.ReturnSchemaJson is null)
                Add(changes, effectiveLimits, new AiToolChange(AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, oldTool.Name, "$.returnSchema", "The return schema was removed."));
            else if (oldTool.ReturnSchemaJson is not null && newTool.ReturnSchemaJson is not null)
            {
                using var oldReturn = JsonDocument.Parse(oldTool.ReturnSchemaJson);
                using var newReturn = JsonDocument.Parse(newTool.ReturnSchemaJson);
                CompareSchema(oldReturn.RootElement, newReturn.RootElement, oldTool.Name, "$.returnSchema", false, changes, diagnostics, effectiveLimits);
            }
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

    private static void Add(List<AiToolChange> changes, AiToolContractLimits limits, AiToolChange change)
    {
        if (changes.Count >= limits.MaxChanges)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The comparison exceeds the configured change-count limit."));
        changes.Add(change);
    }

    private static void CompareSchema(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits)
    {
        var oldRaw = oldSchema.GetRawText();
        var newRaw = newSchema.GetRawText();
        if (string.Equals(oldRaw, newRaw, StringComparison.Ordinal))
            return;
        if (EquivalentIgnoringSchemaSetOrder(oldSchema, newSchema))
            return;

        var changesBefore = changes.Count;
        CompareType(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareNullable(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareEnum(oldSchema, newSchema, toolName, path, input, changes, limits);
        CompareConstraints(oldSchema, newSchema, toolName, path, input, changes, limits);

        if (oldSchema.ValueKind == JsonValueKind.Object && newSchema.ValueKind == JsonValueKind.Object)
        {
            CompareObjectProperties(oldSchema, newSchema, toolName, path, input, changes, diagnostics, limits);
            CompareRequired(oldSchema, newSchema, toolName, path, input, changes, limits);
            CompareNestedSchemas(oldSchema, newSchema, toolName, path, input, changes, diagnostics, limits);
        }

        if (changes.Count == changesBefore && !string.Equals(oldRaw, newRaw, StringComparison.Ordinal))
        {
            var change = new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path, "The schema changed in a way that cannot be classified safely.");
            Add(changes, limits, change);
            diagnostics.Add(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "Schema change at " + path + " requires review because its semantics are unsupported."));
        }
    }

    private static void CompareObjectProperties(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits)
    {
        var oldProperties = GetObject(oldSchema, "properties");
        var newProperties = GetObject(newSchema, "properties");
        if (oldProperties is null && newProperties is null)
            return;

        oldProperties ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        newProperties ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var oldProperty in oldProperties)
        {
            if (!newProperties.TryGetValue(oldProperty.Key, out var newProperty))
            {
                var kind = input ? AiToolChangeKind.ParameterRemoved : AiToolChangeKind.ReturnSchemaBreaking;
                var compatibility = AiToolCompatibility.Breaking;
                Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + ".properties." + oldProperty.Key, "Property '" + oldProperty.Key + "' was removed."));
            }
            else
            {
                CompareSchema(oldProperty.Value, newProperty, toolName, path + ".properties." + oldProperty.Key, input, changes, diagnostics, limits);
            }
        }

        foreach (var newProperty in newProperties)
        {
            if (oldProperties.ContainsKey(newProperty.Key))
                continue;
            var required = IsRequired(newSchema, newProperty.Key);
            if (!input)
            {
                Add(changes, limits, new AiToolChange(AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, toolName, path + ".properties." + newProperty.Key, "Return property '" + newProperty.Key + "' was added."));
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

    private static void CompareRequired(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        var oldRequired = GetStringSet(oldSchema, "required");
        var newRequired = GetStringSet(newSchema, "required");
        foreach (var name in newRequired.Except(oldRequired, StringComparer.Ordinal))
        {
            if (GetObject(oldSchema, "properties")?.ContainsKey(name) == true)
                Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.RequiredParameterAdded : AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, toolName, path + ".required", "Property '" + name + "' became required."));
        }
        foreach (var name in oldRequired.Except(newRequired, StringComparer.Ordinal))
        {
            if (input)
                Add(changes, limits, new AiToolChange(AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, toolName, path + ".required", "Parameter '" + name + "' is no longer required."));
            else
                Add(changes, limits, new AiToolChange(AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, toolName, path + ".required", "Return property '" + name + "' is no longer required."));
        }
    }

    private static void CompareNestedSchemas(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, List<AiToolContractDiagnostic> diagnostics, AiToolContractLimits limits)
    {
        foreach (var name in new[] { "items", "additionalProperties", "contains", "not" })
        {
            if (oldSchema.TryGetProperty(name, out var oldChild) && newSchema.TryGetProperty(name, out var newChild))
                CompareSchema(oldChild, newChild, toolName, path + "." + name, input, changes, diagnostics, limits);
            else if (oldSchema.TryGetProperty(name, out _) != newSchema.TryGetProperty(name, out _))
            {
                Add(changes, limits, new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path + "." + name, "A schema keyword changed in an unsupported position."));
                diagnostics.Add(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "Schema keyword " + name + " at " + path + " requires review."));
            }
        }

        foreach (var name in new[] { "oneOf", "anyOf", "allOf", "$defs", "definitions" })
        {
            var oldExists = oldSchema.TryGetProperty(name, out var oldChild);
            var newExists = newSchema.TryGetProperty(name, out var newChild);
            if (oldExists || newExists)
            {
                if (!oldExists || !newExists || !string.Equals(oldChild.GetRawText(), newChild.GetRawText(), StringComparison.Ordinal))
                {
                    Add(changes, limits, new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path + "." + name, "Reference/composition schema changes require review."));
                    diagnostics.Add(new AiToolContractDiagnostic(AiToolDiagnosticKind.UnsupportedClassification, "Schema composition keyword " + name + " changed at " + path + "."));
                }
            }
        }
    }

    private static void CompareType(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        var oldType = GetTypeSet(oldSchema);
        var newType = GetTypeSet(newSchema);
        if (oldType.SetEquals(newType))
            return;
        oldType.Remove("null");
        newType.Remove("null");
        if (oldType.SetEquals(newType))
            return;
        var compatibility = AiToolCompatibility.Breaking;
        var kind = input ? AiToolChangeKind.TypeChanged : AiToolChangeKind.ReturnSchemaBreaking;
        Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + ".type", "Schema type changed."));
    }

    private static void CompareNullable(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        var oldNullable = GetNullable(oldSchema);
        var newNullable = GetNullable(newSchema);
        if (oldNullable == newNullable)
            return;
        var narrowed = oldNullable && !newNullable;
        var kind = narrowed ? AiToolChangeKind.ConstraintNarrowed : AiToolChangeKind.ConstraintExpanded;
        var compatibility = narrowed ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
        if (!input)
            kind = narrowed ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
        Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path, "Nullability changed."));
    }

    private static void CompareEnum(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        var oldEnum = GetArraySet(oldSchema, "enum");
        var newEnum = GetArraySet(newSchema, "enum");
        if (oldEnum is null || newEnum is null || oldEnum.SetEquals(newEnum))
            return;
        if (newEnum.IsSubsetOf(oldEnum))
            Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.EnumNarrowed : AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking, toolName, path + ".enum", "Enum values were narrowed."));
        else if (oldEnum.IsSubsetOf(newEnum))
            Add(changes, limits, new AiToolChange(input ? AiToolChangeKind.EnumExpanded : AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive, toolName, path + ".enum", "Enum values were expanded."));
        else
        {
            Add(changes, limits, new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path + ".enum", "Enum values changed in both directions and cannot be classified safely."));
        }
    }

    private static void CompareConstraints(JsonElement oldSchema, JsonElement newSchema, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        foreach (var name in new[] { "minimum", "exclusiveMinimum", "minLength", "minItems" })
            CompareBound(oldSchema, newSchema, name, true, toolName, path, input, changes, limits);
        foreach (var name in new[] { "maximum", "exclusiveMaximum", "maxLength", "maxItems" })
            CompareBound(oldSchema, newSchema, name, false, toolName, path, input, changes, limits);
    }

    private static void CompareBound(JsonElement oldSchema, JsonElement newSchema, string name, bool lowerBound, string toolName, string path, bool input, List<AiToolChange> changes, AiToolContractLimits limits)
    {
        var oldExists = oldSchema.TryGetProperty(name, out var oldValue);
        var newExists = newSchema.TryGetProperty(name, out var newValue);
        if (!oldExists && !newExists)
            return;
        var narrowed = false;
        var expanded = false;
        if (!oldExists && newExists)
            narrowed = lowerBound;
        else if (oldExists && !newExists)
            expanded = lowerBound;
        else if (TryNumber(oldValue, out var oldNumber) && TryNumber(newValue, out var newNumber))
        {
            narrowed = lowerBound ? newNumber > oldNumber : newNumber < oldNumber;
            expanded = lowerBound ? newNumber < oldNumber : newNumber > oldNumber;
        }
        else if (!string.Equals(oldValue.GetRawText(), newValue.GetRawText(), StringComparison.Ordinal))
        {
            Add(changes, limits, new AiToolChange(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, toolName, path + "." + name, "Constraint changed in a non-numeric form."));
            return;
        }

        if (!narrowed && !expanded)
            return;
        var kind = narrowed ? AiToolChangeKind.ConstraintNarrowed : AiToolChangeKind.ConstraintExpanded;
        var compatibility = narrowed ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
        if (!input)
            kind = narrowed ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive;
        Add(changes, limits, new AiToolChange(kind, compatibility, toolName, path + "." + name, "Constraint changed."));
    }

    private static Dictionary<string, JsonElement>? GetObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return null;
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in property.EnumerateObject())
            result[item.Name] = item.Value;
        return result;
    }

    private static HashSet<string> GetStringSet(JsonElement element, string propertyName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in property.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string value)
                result.Add(value);
        return result;
    }

    private static HashSet<string>? GetArraySet(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return null;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in property.EnumerateArray())
            result.Add(item.GetRawText());
        return result;
    }

    private static bool IsRequired(JsonElement schema, string name) => GetStringSet(schema, "required").Contains(name);

    private static HashSet<string> GetTypeSet(JsonElement element)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!element.TryGetProperty("type", out var type))
            return result;
        if (type.ValueKind == JsonValueKind.String && type.GetString() is string value)
            result.Add(value);
        else if (type.ValueKind == JsonValueKind.Array)
            foreach (var item in type.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string itemValue)
                    result.Add(itemValue);
        return result;
    }

    private static bool GetNullable(JsonElement element)
    {
        if (element.TryGetProperty("nullable", out var nullable) && nullable.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return nullable.GetBoolean();
        return GetTypeSet(element).Contains("null");
    }

    private static bool TryNumber(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
            return true;
        value = default;
        return false;
    }

    private static bool EquivalentIgnoringSchemaSetOrder(JsonElement oldSchema, JsonElement newSchema)
    {
        if (oldSchema.ValueKind != newSchema.ValueKind)
            return false;
        if (oldSchema.ValueKind == JsonValueKind.Object)
        {
            var oldProperties = oldSchema.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
            var newProperties = newSchema.EnumerateObject().ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
            if (oldProperties.Count != newProperties.Count)
                return false;
            foreach (var oldProperty in oldProperties)
            {
                if (!newProperties.TryGetValue(oldProperty.Key, out var newProperty))
                    return false;
                if ((string.Equals(oldProperty.Key, "required", StringComparison.Ordinal) || string.Equals(oldProperty.Key, "enum", StringComparison.Ordinal)) && oldProperty.Value.ValueKind == JsonValueKind.Array && newProperty.ValueKind == JsonValueKind.Array)
                {
                    if (!GetRawArraySet(oldProperty.Value).SetEquals(GetRawArraySet(newProperty)))
                        return false;
                }
                else if (!EquivalentIgnoringSchemaSetOrder(oldProperty.Value, newProperty))
                {
                    return false;
                }
            }
            return true;
        }
        if (oldSchema.ValueKind == JsonValueKind.Array)
        {
            var oldItems = oldSchema.EnumerateArray().ToList();
            var newItems = newSchema.EnumerateArray().ToList();
            if (oldItems.Count != newItems.Count)
                return false;
            for (var index = 0; index < oldItems.Count; index++)
                if (!EquivalentIgnoringSchemaSetOrder(oldItems[index], newItems[index]))
                    return false;
            return true;
        }
        return string.Equals(oldSchema.GetRawText(), newSchema.GetRawText(), StringComparison.Ordinal);
    }

    private static HashSet<string> GetRawArraySet(JsonElement element)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
            result.Add(item.GetRawText());
        return result;
    }
}
