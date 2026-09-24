using System.Reflection;

namespace KeelMatrix.AiToolContract;

internal enum SchemaTransitionDirection
{
    Added,
    Removed,
    Changed,
    Expanded,
    Narrowed,
    Widened,
    OptionalPropertyAdded,
    RequiredPropertyAdded
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
internal sealed class SchemaTransitionModelPropertyAttribute : Attribute
{
    internal SchemaTransitionModelPropertyAttribute(params SchemaTransitionDirection[] directions)
    {
        Directions = directions;
    }

    internal IReadOnlyList<SchemaTransitionDirection> Directions { get; }
}

internal readonly struct SchemaTransitionExpectation
{
    internal SchemaTransitionExpectation(AiToolChangeKind kind, AiToolCompatibility compatibility, bool unsupported)
    {
        Kind = kind;
        Compatibility = compatibility;
        Unsupported = unsupported;
    }

    internal AiToolChangeKind Kind { get; }
    internal AiToolCompatibility Compatibility { get; }
    internal bool Unsupported { get; }
}

internal sealed class SchemaTransitionRule
{
    internal SchemaTransitionRule(
        string propertyName,
        SchemaTransitionDirection direction,
        SchemaTransitionExpectation input,
        SchemaTransitionExpectation returnSchema)
    {
        PropertyName = propertyName;
        Direction = direction;
        Input = input;
        Return = returnSchema;
    }

    internal string PropertyName { get; }
    internal SchemaTransitionDirection Direction { get; }
    internal SchemaTransitionExpectation Input { get; }
    internal SchemaTransitionExpectation Return { get; }
}

internal sealed class SchemaTransition
{
    internal SchemaTransition(string propertyName, SchemaTransitionDirection direction)
    {
        PropertyName = propertyName;
        Direction = direction;
    }

    internal string PropertyName { get; }
    internal SchemaTransitionDirection Direction { get; }
}

/// <summary>
/// The explicit compatibility rule table is keyed by the normalized schema model.
/// Reflection checks that its property and direction keys stay equal to the model
/// annotations; comparison reports any uncovered key as unsupported.
/// </summary>
internal static class SchemaTransitionRules
{
    private static readonly IReadOnlyList<SchemaTransitionRule> RuleTable = new[]
    {
        Unsupported(nameof(NormalizedSchema.Reference), SchemaTransitionDirection.Added),
        Unsupported(nameof(NormalizedSchema.Reference), SchemaTransitionDirection.Removed),
        Unsupported(nameof(NormalizedSchema.Reference), SchemaTransitionDirection.Changed),

        Unsupported(nameof(NormalizedSchema.HasDefault), SchemaTransitionDirection.Added),
        Unsupported(nameof(NormalizedSchema.HasDefault), SchemaTransitionDirection.Removed),
        Unsupported(nameof(NormalizedSchema.HasDefault), SchemaTransitionDirection.Changed),

        Same(nameof(NormalizedSchema.Description), SchemaTransitionDirection.Added, AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky),
        Same(nameof(NormalizedSchema.Description), SchemaTransitionDirection.Removed, AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky),
        Same(nameof(NormalizedSchema.Description), SchemaTransitionDirection.Changed, AiToolChangeKind.DescriptionChanged, AiToolCompatibility.Risky),

        Unsupported(nameof(NormalizedSchema.DefaultValue), SchemaTransitionDirection.Added),
        Unsupported(nameof(NormalizedSchema.DefaultValue), SchemaTransitionDirection.Removed),
        Unsupported(nameof(NormalizedSchema.DefaultValue), SchemaTransitionDirection.Changed),
        Unsupported(nameof(NormalizedSchema.Format), SchemaTransitionDirection.Added),
        Unsupported(nameof(NormalizedSchema.Format), SchemaTransitionDirection.Removed),
        Unsupported(nameof(NormalizedSchema.Format), SchemaTransitionDirection.Changed),

        InputReturn(nameof(NormalizedSchema.Types), SchemaTransitionDirection.Added, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        InputReturn(nameof(NormalizedSchema.Types), SchemaTransitionDirection.Removed, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        InputReturn(nameof(NormalizedSchema.Types), SchemaTransitionDirection.Expanded, AiToolChangeKind.TypeChanged, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        InputReturn(nameof(NormalizedSchema.Types), SchemaTransitionDirection.Narrowed, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        InputReturn(nameof(NormalizedSchema.Types), SchemaTransitionDirection.Changed, AiToolChangeKind.TypeChanged, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),

        InputReturn(nameof(NormalizedSchema.Properties), SchemaTransitionDirection.OptionalPropertyAdded, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        InputReturn(nameof(NormalizedSchema.Properties), SchemaTransitionDirection.RequiredPropertyAdded, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        InputReturn(nameof(NormalizedSchema.Properties), SchemaTransitionDirection.Removed, AiToolChangeKind.ParameterRemoved, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),

        InputReturn(nameof(NormalizedSchema.Required), SchemaTransitionDirection.Added, AiToolChangeKind.RequiredParameterAdded, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        InputReturn(nameof(NormalizedSchema.Required), SchemaTransitionDirection.Removed, AiToolChangeKind.OptionalParameterAdded, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        Unsupported(nameof(NormalizedSchema.Required), SchemaTransitionDirection.Changed),

        InputReturn(nameof(NormalizedSchema.EnumValues), SchemaTransitionDirection.Added, AiToolChangeKind.EnumNarrowed, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        InputReturn(nameof(NormalizedSchema.EnumValues), SchemaTransitionDirection.Removed, AiToolChangeKind.EnumExpanded, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        InputReturn(nameof(NormalizedSchema.EnumValues), SchemaTransitionDirection.Expanded, AiToolChangeKind.EnumExpanded, AiToolCompatibility.Additive, AiToolChangeKind.ReturnSchemaAdditive, AiToolCompatibility.Additive),
        InputReturn(nameof(NormalizedSchema.EnumValues), SchemaTransitionDirection.Narrowed, AiToolChangeKind.EnumNarrowed, AiToolCompatibility.Breaking, AiToolChangeKind.ReturnSchemaBreaking, AiToolCompatibility.Breaking),
        Unsupported(nameof(NormalizedSchema.EnumValues), SchemaTransitionDirection.Changed),

        Unsupported(nameof(NormalizedSchema.Items), SchemaTransitionDirection.Added),
        Unsupported(nameof(NormalizedSchema.Items), SchemaTransitionDirection.Removed),

        Constraint(nameof(NormalizedSchema.Minimum), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.Minimum), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.Minimum), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.Minimum), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.Maximum), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.Maximum), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.Maximum), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.Maximum), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.ExclusiveMinimum), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.ExclusiveMinimum), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.ExclusiveMinimum), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.ExclusiveMinimum), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.ExclusiveMaximum), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.ExclusiveMaximum), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.ExclusiveMaximum), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.ExclusiveMaximum), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.MinLength), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.MinLength), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.MinLength), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.MinLength), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.MaxLength), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.MaxLength), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.MaxLength), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.MaxLength), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.MinItems), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.MinItems), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.MinItems), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.MinItems), SchemaTransitionDirection.Widened),
        Constraint(nameof(NormalizedSchema.MaxItems), SchemaTransitionDirection.Added),
        Constraint(nameof(NormalizedSchema.MaxItems), SchemaTransitionDirection.Removed),
        Constraint(nameof(NormalizedSchema.MaxItems), SchemaTransitionDirection.Narrowed),
        Constraint(nameof(NormalizedSchema.MaxItems), SchemaTransitionDirection.Widened)
    };

    private static readonly IReadOnlyDictionary<string, PropertyInfo> ModelProperties =
        typeof(NormalizedSchema)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .ToDictionary(static property => property.Name, StringComparer.Ordinal);

    internal static IReadOnlyList<SchemaTransitionRule> Rules => RuleTable;
    internal static IReadOnlyCollection<string> ModelPropertyNames => ModelProperties.Keys.ToArray();

    internal static IReadOnlyList<string> CoverageFailures
    {
        get
        {
            var failures = new List<string>();
            var ruleProperties = new HashSet<string>(RuleTable.Select(static rule => rule.PropertyName), StringComparer.Ordinal);
            foreach (var property in ModelProperties.Keys)
                if (!ruleProperties.Contains(property))
                    failures.Add("Missing rule table property '" + property + "'.");
            foreach (var property in ruleProperties)
                if (!ModelProperties.ContainsKey(property))
                    failures.Add("Rule table property '" + property + "' is not in the normalized model.");

            foreach (var property in ModelProperties)
            {
                var annotation = property.Value.GetCustomAttribute<SchemaTransitionModelPropertyAttribute>();
                if (annotation is null)
                {
                    failures.Add("Normalized model property '" + property.Key + "' has no transition-direction annotation.");
                    continue;
                }
                var modelDirections = annotation.Directions;
                var ruleDirections = new HashSet<SchemaTransitionDirection>(RuleTable
                    .Where(rule => string.Equals(rule.PropertyName, property.Key, StringComparison.Ordinal))
                    .Select(static rule => rule.Direction)
                    );
                foreach (var direction in modelDirections.Where(direction => !ruleDirections.Contains(direction)))
                    failures.Add("Missing rule table direction '" + property.Key + ":" + direction + "'.");
                foreach (var direction in ruleDirections.Where(direction => !modelDirections.Contains(direction)))
                    failures.Add("Rule table direction '" + property.Key + ":" + direction + "' is not in the normalized model.");
            }

            return failures;
        }
    }

    internal static bool TryGetRule(string propertyName, SchemaTransitionDirection direction, out SchemaTransitionRule? rule)
    {
        rule = RuleTable.FirstOrDefault(candidate =>
            string.Equals(candidate.PropertyName, propertyName, StringComparison.Ordinal) && candidate.Direction == direction);
        return rule is not null;
    }

    internal static IReadOnlyList<SchemaTransition> FindTransitions(NormalizedSchema oldSchema, NormalizedSchema newSchema)
    {
        var transitions = new List<SchemaTransition>();
        foreach (var property in ModelProperties)
        {
            var oldValue = property.Value.GetValue(oldSchema);
            var newValue = property.Value.GetValue(newSchema);
            if (ValuesEqual(property.Key, oldValue, newValue))
                continue;

            var direction = GetDirection(property.Key, oldValue, newValue, newSchema);
            if (direction.HasValue)
                transitions.Add(new SchemaTransition(property.Key, direction.Value));
        }
        return transitions;
    }

    private static SchemaTransitionRule Same(string propertyName, SchemaTransitionDirection direction, AiToolChangeKind kind, AiToolCompatibility compatibility) =>
        InputReturn(propertyName, direction, kind, compatibility, kind, compatibility);

    private static SchemaTransitionRule Unsupported(string propertyName, SchemaTransitionDirection direction) =>
        new(propertyName, direction, new SchemaTransitionExpectation(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true), new SchemaTransitionExpectation(AiToolChangeKind.Unsupported, AiToolCompatibility.Risky, true));

    private static SchemaTransitionRule InputReturn(
        string propertyName,
        SchemaTransitionDirection direction,
        AiToolChangeKind inputKind,
        AiToolCompatibility inputCompatibility,
        AiToolChangeKind returnKind,
        AiToolCompatibility returnCompatibility) =>
        new(propertyName, direction,
            new SchemaTransitionExpectation(inputKind, inputCompatibility, false),
            new SchemaTransitionExpectation(returnKind, returnCompatibility, false));

    private static SchemaTransitionRule Constraint(string propertyName, SchemaTransitionDirection direction)
    {
        var narrowed = direction is SchemaTransitionDirection.Added or SchemaTransitionDirection.Narrowed;
        var kind = narrowed ? AiToolChangeKind.ConstraintNarrowed : AiToolChangeKind.ConstraintExpanded;
        var compatibility = narrowed ? AiToolCompatibility.Breaking : AiToolCompatibility.Additive;
        return InputReturn(propertyName, direction, kind, compatibility,
            narrowed ? AiToolChangeKind.ReturnSchemaBreaking : AiToolChangeKind.ReturnSchemaAdditive,
            compatibility);
    }

    private static bool ValuesEqual(string propertyName, object? oldValue, object? newValue)
    {
        if (ReferenceEquals(oldValue, newValue))
            return true;
        if (oldValue is null || newValue is null)
            return false;
        if (oldValue is string oldString && newValue is string newString)
            return string.Equals(oldString, newString, StringComparison.Ordinal);
        if (oldValue is JsonNumber oldNumber && newValue is JsonNumber newNumber)
            return oldNumber.CompareTo(newNumber) == 0;
        if (oldValue is NormalizedJsonValue oldJsonValue && newValue is NormalizedJsonValue newJsonValue)
            return oldJsonValue.SemanticallyEquals(newJsonValue);
        if (oldValue is IReadOnlyList<string> oldStrings && newValue is IReadOnlyList<string> newStrings)
            return oldStrings.SequenceEqual(newStrings, StringComparer.Ordinal);
        if (oldValue is IReadOnlyList<NormalizedJsonValue> oldValues && newValue is IReadOnlyList<NormalizedJsonValue> newValues)
            return oldValues.Count == newValues.Count && oldValues.Zip(newValues, static (left, right) => left.SemanticallyEquals(right)).All(static equal => equal);
        if (oldValue is IReadOnlyDictionary<string, NormalizedSchema> oldProperties && newValue is IReadOnlyDictionary<string, NormalizedSchema> newProperties)
            return oldProperties.Count == newProperties.Count && oldProperties.All(property => newProperties.TryGetValue(property.Key, out var other) && property.Value.SemanticallyEquals(other));
        return Equals(oldValue, newValue);
    }

    private static SchemaTransitionDirection? GetDirection(string propertyName, object? oldValue, object? newValue, NormalizedSchema newSchema)
    {
        if (propertyName == nameof(NormalizedSchema.Types))
            return SetDirection((IReadOnlyList<string>)oldValue!, (IReadOnlyList<string>)newValue!);
        if (propertyName == nameof(NormalizedSchema.EnumValues))
        {
            if (oldValue is null)
                return SchemaTransitionDirection.Added;
            if (newValue is null)
                return SchemaTransitionDirection.Removed;
            return ValueSetDirection((IReadOnlyList<NormalizedJsonValue>)oldValue, (IReadOnlyList<NormalizedJsonValue>)newValue);
        }
        if (propertyName == nameof(NormalizedSchema.Properties))
            return PropertyDirection((IReadOnlyDictionary<string, NormalizedSchema>)oldValue!, (IReadOnlyDictionary<string, NormalizedSchema>)newValue!, newSchema.Required);
        if (propertyName == nameof(NormalizedSchema.Items))
            return oldValue is null ? SchemaTransitionDirection.Added : newValue is null ? SchemaTransitionDirection.Removed : null;
        if (propertyName == nameof(NormalizedSchema.Required))
            return RequiredDirection((IReadOnlyList<string>)oldValue!, (IReadOnlyList<string>)newValue!);
        if (propertyName == nameof(NormalizedSchema.HasDefault))
            return (bool)oldValue! ? SchemaTransitionDirection.Removed : SchemaTransitionDirection.Added;
        if (propertyName is nameof(NormalizedSchema.Minimum) or nameof(NormalizedSchema.Maximum) or nameof(NormalizedSchema.ExclusiveMinimum) or nameof(NormalizedSchema.ExclusiveMaximum) or nameof(NormalizedSchema.MinLength) or nameof(NormalizedSchema.MaxLength) or nameof(NormalizedSchema.MinItems) or nameof(NormalizedSchema.MaxItems))
        {
            var oldNumber = oldValue is JsonNumber oldNumberValue ? oldNumberValue : (JsonNumber?)null;
            var newNumber = newValue is JsonNumber newNumberValue ? newNumberValue : (JsonNumber?)null;
            if (!oldNumber.HasValue)
                return SchemaTransitionDirection.Added;
            if (!newNumber.HasValue)
                return SchemaTransitionDirection.Removed;
            var comparison = newNumber.Value.CompareTo(oldNumber.Value);
            if (comparison == 0)
                return null;
            var lowerBound = propertyName is nameof(NormalizedSchema.Minimum) or nameof(NormalizedSchema.ExclusiveMinimum) or nameof(NormalizedSchema.MinLength) or nameof(NormalizedSchema.MinItems);
            return (lowerBound ? comparison > 0 : comparison < 0) ? SchemaTransitionDirection.Narrowed : SchemaTransitionDirection.Widened;
        }

        if (oldValue is null)
            return SchemaTransitionDirection.Added;
        if (newValue is null)
            return SchemaTransitionDirection.Removed;
        return SchemaTransitionDirection.Changed;
    }

    private static SchemaTransitionDirection SetDirection(IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues)
    {
        if (oldValues.Count == 0)
            return SchemaTransitionDirection.Added;
        if (newValues.Count == 0)
            return SchemaTransitionDirection.Removed;
        var oldSet = new HashSet<string>(oldValues, StringComparer.Ordinal);
        var newSet = new HashSet<string>(newValues, StringComparer.Ordinal);
        return newSet.IsSubsetOf(oldSet)
            ? SchemaTransitionDirection.Narrowed
            : oldSet.IsSubsetOf(newSet)
                ? SchemaTransitionDirection.Expanded
                : SchemaTransitionDirection.Changed;
    }

    private static SchemaTransitionDirection RequiredDirection(IReadOnlyList<string> oldValues, IReadOnlyList<string> newValues)
    {
        if (oldValues.Count == 0)
            return SchemaTransitionDirection.Added;
        if (newValues.Count == 0)
            return SchemaTransitionDirection.Removed;

        var oldSet = new HashSet<string>(oldValues, StringComparer.Ordinal);
        var newSet = new HashSet<string>(newValues, StringComparer.Ordinal);
        return oldSet.IsSubsetOf(newSet)
            ? SchemaTransitionDirection.Added
            : newSet.IsSubsetOf(oldSet)
                ? SchemaTransitionDirection.Removed
                : SchemaTransitionDirection.Changed;
    }

    private static SchemaTransitionDirection ValueSetDirection(IReadOnlyList<NormalizedJsonValue> oldValues, IReadOnlyList<NormalizedJsonValue> newValues)
    {
        var narrowed = newValues.All(value => oldValues.Any(value.SemanticallyEquals)) && newValues.Count < oldValues.Count;
        var expanded = oldValues.All(value => newValues.Any(value.SemanticallyEquals)) && oldValues.Count < newValues.Count;
        return narrowed ? SchemaTransitionDirection.Narrowed : expanded ? SchemaTransitionDirection.Expanded : SchemaTransitionDirection.Changed;
    }

    private static SchemaTransitionDirection? PropertyDirection(IReadOnlyDictionary<string, NormalizedSchema> oldProperties, IReadOnlyDictionary<string, NormalizedSchema> newProperties, IReadOnlyList<string>? newRequired)
    {
        var added = newProperties.Keys.Except(oldProperties.Keys, StringComparer.Ordinal).ToArray();
        var removed = oldProperties.Keys.Except(newProperties.Keys, StringComparer.Ordinal).ToArray();
        if (added.Length == 1 && removed.Length == 0 && oldProperties.All(property => newProperties.TryGetValue(property.Key, out var other) && property.Value.SemanticallyEquals(other)))
        {
            var required = newRequired?.Contains(added[0], StringComparer.Ordinal) == true;
            return required ? SchemaTransitionDirection.RequiredPropertyAdded : SchemaTransitionDirection.OptionalPropertyAdded;
        }
        if (removed.Length == 1 && added.Length == 0 && newProperties.All(property => oldProperties.TryGetValue(property.Key, out var other) && property.Value.SemanticallyEquals(other)))
            return SchemaTransitionDirection.Removed;
        return added.Length == 0 && removed.Length == 0 ? null : SchemaTransitionDirection.Changed;
    }
}
