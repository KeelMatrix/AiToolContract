using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace KeelMatrix.AiToolContract.Tests;

public sealed class UnicodePersistenceTests
{
    private static readonly string[] MalformedStrings = { "\ud800", "\udfff", "\ud800\ud800", "\udfff\udfff", "\udfff\ud800", "a\ud800z", "a\udfffz", "\ud800\udc00\ud800", "\udfff\ud800\udc00", "\ufffd\ud800" };

    public static IEnumerable<object[]> InvalidStrings()
    {
        // Pass an index: test discovery itself can replace malformed Unicode.
        for (var index = 0; index < MalformedStrings.Length; index++)
            yield return new object[] { index };
    }

    [Theory]
    [MemberData(nameof(InvalidStrings))]
    public void CaptureRejectsMalformedIdentityAndDescriptionWithoutPartialBaseline(int caseIndex)
    {
        var value = MalformedStrings[caseIndex];
        using var schema = JsonDocument.Parse("{}");
        foreach (var isName in new[] { false, true })
        {
            var invalid = AIFunctionFactory.CreateDeclaration(isName ? value : "tool", isName ? null : value, schema.RootElement);
            var valid = AIFunctionFactory.CreateDeclaration("\ufffd", "valid", schema.RootElement);
            foreach (var catalog in new AITool[][] { new AITool[] { invalid }, new AITool[] { valid, invalid }, new AITool[] { invalid, valid } })
            {
                var result = AiToolContractCapture.Capture(catalog);
                Assert.False(result.Succeeded);
                Assert.Null(result.Baseline);
                Assert.Equal(AiToolDiagnosticKind.CaptureFailure, result.Diagnostic!.Kind);
            }
        }
        var collision = AiToolContractCapture.Capture(new AITool[]
        {
            AIFunctionFactory.CreateDeclaration(value, null, schema.RootElement),
            AIFunctionFactory.CreateDeclaration("\ufffd", null, schema.RootElement),
            AIFunctionFactory.CreateDeclaration("\udfff", null, schema.RootElement)
        });
        Assert.False(collision.Succeeded);
        Assert.Equal(AiToolDiagnosticKind.CaptureFailure, collision.Diagnostic!.Kind);
    }

    [Theory]
    [MemberData(nameof(InvalidStrings))]
    public void LiteralAndEscapedMalformedBaselineStringsUseMalformedDiagnostic(int caseIndex)
    {
        var value = MalformedStrings[caseIndex];
        var escaped = string.Concat(value.Select(c => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var text in new[] { value, escaped })
            foreach (var location in Enumerable.Range(0, 11))
            {
                var name = location == 0 ? text : "tool";
                var description = location == 1 ? "\"" + text + "\"" : "null";
                var schema = SchemaString(location, text);
                var baseline = "{\"schemaVersion\":1,\"tools\":[{\"name\":\"" + name + "\",\"description\":" + description + ",\"inputSchema\":" + schema + ",\"returnSchema\":" + schema + ",\"requiresApproval\":false}]}";
                var error = Assert.Throws<AiToolContractException>(() => AiToolContractJson.Parse(baseline));
                Assert.Equal(AiToolDiagnosticKind.MalformedBaseline, error.Diagnostic.Kind);
                if (location < 2)
                    continue;
                using var document = JsonDocument.Parse(SchemaString(location, escaped));
                foreach (var output in new[] { false, true })
                {
                    using var empty = JsonDocument.Parse("{}");
                    var capture = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration("tool", null, output ? empty.RootElement : document.RootElement, output ? document.RootElement : null) });
                    Assert.False(capture.Succeeded);
                    Assert.Equal(AiToolDiagnosticKind.CaptureFailure, capture.Diagnostic!.Kind);
                }
            }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("é中文")]
    [InlineData("\ufffd")]
    [InlineData("\ud800\udc00")]
    [InlineData("x\udbff\udfffy")]
    [InlineData("e\u0301")]
    public void ValidUnicodePersistsOrdinallyAtEveryStringSurface(string value)
    {
        var quoted = JsonSerializer.Serialize(value);
        var schemaText = "{\"description\":" + quoted + ",\"format\":" + quoted + ",\"properties\":{" + quoted + ":{}},\"required\":[" + quoted + "],\"enum\":[" + quoted + ", {" + quoted + ":[" + quoted + "]}],\"default\":{" + quoted + ":" + quoted + "}}";
        using var schema = JsonDocument.Parse(schemaText);
        var capture = AiToolContractCapture.Capture(new AITool[] { AIFunctionFactory.CreateDeclaration(value, value, schema.RootElement, schema.RootElement) });
        Assert.True(capture.Succeeded, capture.Diagnostic?.Message);
        var baseline = capture.Baseline!;
        var parsed = AiToolContractJson.Parse(AiToolContractJson.Serialize(baseline));
        Assert.Equal(value, parsed.Tools[0].Name);
        Assert.Equal(value, parsed.Tools[0].Description);
        Assert.True(AiToolContractVerifier.Compare(baseline, parsed).IsClean);
        Assert.Equal(AiToolContractJson.Serialize(baseline), AiToolContractJson.Serialize(parsed));
    }

    private static string SchemaString(int location, string text) => location switch
    {
        0 or 1 => "{}",
        2 => "{\"description\":\"" + text + "\"}",
        3 => "{\"format\":\"" + text + "\"}",
        4 => "{\"$ref\":\"#/" + text + "\"}",
        5 => "{\"required\":[\"" + text + "\"]}",
        6 => "{\"properties\":{\"" + text + "\":{}}}",
        7 => "{\"enum\":[\"" + text + "\"]}",
        8 => "{\"default\":{\"" + text + "\":0}}",
        9 => "{\"default\":[\"" + text + "\"]}",
        10 => "{\"enum\":[{\"" + text + "\":[0]}]}",
        _ => throw new ArgumentOutOfRangeException(nameof(location))
    };
}
