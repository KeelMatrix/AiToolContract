namespace KeelMatrix.AiToolContract;

internal enum EnumSetRelation
{
    Equal,
    Narrowed,
    Expanded,
    Changed
}

internal static class EnumValueSet
{
    // Enum values have unique, ordinally sorted canonical semantic keys. Merge
    // once; neither numeric formatting nor nested membership scans are needed.
    internal static EnumSetRelation Compare(IReadOnlyList<NormalizedJsonValue>? previous, IReadOnlyList<NormalizedJsonValue>? candidate)
    {
        if (previous is null)
            return candidate is null ? EnumSetRelation.Equal : EnumSetRelation.Narrowed;
        if (candidate is null)
            return EnumSetRelation.Expanded;
        var oldIndex = 0;
        var newIndex = 0;
        var removed = false;
        var added = false;
        while (oldIndex < previous.Count && newIndex < candidate.Count)
        {
            var comparison = StringComparer.Ordinal.Compare(previous[oldIndex].EnumKey, candidate[newIndex].EnumKey);
            if (comparison == 0)
            {
                oldIndex++;
                newIndex++;
            }
            else if (comparison < 0)
            {
                removed = true;
                oldIndex++;
            }
            else
            {
                added = true;
                newIndex++;
            }
            if (removed && added)
                return EnumSetRelation.Changed;
        }
        removed |= oldIndex < previous.Count;
        added |= newIndex < candidate.Count;
        return removed ? added ? EnumSetRelation.Changed : EnumSetRelation.Narrowed : added ? EnumSetRelation.Expanded : EnumSetRelation.Equal;
    }
}
