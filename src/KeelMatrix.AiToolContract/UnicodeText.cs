namespace KeelMatrix.AiToolContract;

internal static class UnicodeText
{
    internal static bool IsWellFormed(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character) || ++index == value.Length || !char.IsLowSurrogate(value[index]))
                return false;
        }
        return true;
    }
}
