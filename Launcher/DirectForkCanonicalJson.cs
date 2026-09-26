using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZZZScannerHelper;

internal static partial class Program
{
    internal static string BrowserCanonicalJson(JsonNode node) => BrowserCanonicalize(node).ToJsonString(
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonNode BrowserCanonicalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            KeyValuePair.Create(pair.Key, pair.Value is null ? null : BrowserCanonicalize(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(value => value is null ? null : BrowserCanonicalize(value)).ToArray()),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => JsonValue.Create(value.GetValue<double>())!,
        _ => node.DeepClone()
    };
}
