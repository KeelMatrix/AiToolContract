using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeelMatrix.AiToolContract;

// The same encoder and byte accounting serve measurement and output. Measurement
// stops at the first overflow without allocating an escaped copy of the string.
internal sealed class CanonicalJsonWriter
{
    private readonly long _limit;
    private readonly StringBuilder? _builder;
    private long _bytes;

    internal CanonicalJsonWriter(long limit = int.MaxValue, bool countOnly = false)
    {
        _limit = limit;
        _builder = countOnly ? null : new StringBuilder();
    }

    internal void Append(char value)
    {
        Reserve(1);
        _builder?.Append(value);
    }

    internal void Append(string value)
    {
        Reserve(Encoding.UTF8.GetByteCount(value));
        _builder?.Append(value);
    }

    internal void AppendString(string value)
    {
        Reserve(2);
        foreach (var character in value)
        {
            var length = character is '\\' or '\b' or '\t' or '\n' or '\f' or '\r' ? 2
                : char.IsSurrogate(character) || JavaScriptEncoder.Default.WillEncode(character) ? 6 : 1;
            Reserve(length);
        }
        if (_builder is not null)
            _builder.Append(JsonSerializer.Serialize(value));
    }

    private void Reserve(long count)
    {
        if (count > _limit - _bytes)
            throw new AiToolContractException(new AiToolContractDiagnostic(AiToolDiagnosticKind.CanonicalizationOrResourceLimit, "The canonical JSON exceeds the configured UTF-8 byte limit."));
        _bytes += count;
    }

    public override string ToString() => _builder?.ToString() ?? string.Empty;
}
