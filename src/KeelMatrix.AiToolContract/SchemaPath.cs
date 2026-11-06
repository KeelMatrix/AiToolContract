using System.Text.Json;

namespace KeelMatrix.AiToolContract;

internal static class SchemaPath
{
    internal static string Property(string path, string name) => path + "[" + JsonSerializer.Serialize(name) + "]";

    internal static string Index(string path, int index) => path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
}
