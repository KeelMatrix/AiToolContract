using System.Text.Json;

namespace KeelMatrix.AiToolContract;

internal static class SchemaPath
{
    internal static string Member(string path, string name) => name is
        "$ref" or "description" or "default" or "format" or "type" or "enum" or "items" or "properties" or "required" or
        "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "minLength" or "maxLength" or "minItems" or "maxItems"
        ? path + "." + name
        : Property(path, name);

    internal static string Property(string path, string name) => path + "[" + JsonSerializer.Serialize(name) + "]";

    internal static string Index(string path, int index) => path + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
}
