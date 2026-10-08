using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

internal static partial class R4StagingWriter
{
    private static IReadOnlyList<string> ParserEvidence(
        DriveDiscParseDiagnostic? diagnostic,
        R4Issue? root) => diagnostic is null || root is null
            ? Array.Empty<string>()
            : [
                $"root={root.Code}",
                $"diagnostic={diagnostic.Code}",
                .. (diagnostic.Slot is int slot ? new[] { $"slot={slot}" } : Array.Empty<string>()),
                .. (diagnostic.Candidates.Count > 0
                    ? new[] { $"candidates={string.Join(',', diagnostic.Candidates)}" }
                    : Array.Empty<string>()),
                .. (diagnostic.ObservedCount is int observed && diagnostic.RequiredCount is int required
                    ? new[] { $"substats={observed}/{required}" }
                    : Array.Empty<string>()),
                .. diagnostic.Options.Select(option =>
                    $"option={MapStat(option.Stat, option.Value) ?? option.Stat}|value={NumericValue(option.Value)?.ToString("G17", CultureInfo.InvariantCulture)}|position={option.Position?.ToString(CultureInfo.InvariantCulture) ?? "main"}"),
                .. (diagnostic.MissingPositions.Count > 0
                    ? new[] { $"missingPositions={string.Join(',', diagnostic.MissingPositions)}" }
                    : Array.Empty<string>()),
                $"selectionMode={(diagnostic.Options.Count > 0 ? "structured-options" : "unresolved-only")}"
            ];

    private static bool IsElementalDamageStat(string stat) => stat is
        "物理伤害加成" or "火伤害加成" or "冰伤害加成" or "电伤害加成" or "风伤害加成" or "以太伤害加成"
        or "物理属性伤害加成" or "火属性伤害加成" or "冰属性伤害加成" or "电属性伤害加成" or "风属性伤害加成" or "以太属性伤害加成";

    private static string? MapStat(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var isPercent = value is string text && text.Trim().EndsWith('%');
        var normalized = key.EndsWith('%') || !isPercent ? key : key + "%";
        return StatKeys.TryGetValue(normalized, out var mapped)
            ? mapped
            : StatKeys.TryGetValue(key, out mapped)
                ? mapped
                : key.EndsWith('%') && StatKeys.TryGetValue(key[..^1], out mapped)
                    ? mapped
                    : null;
    }

    private static double? NumericValue(object? value)
    {
        if (value is null) return null;
        if (value is IConvertible convertible && value is not string)
            return convertible.ToDouble(CultureInfo.InvariantCulture);
        var text = value.ToString()?.Trim().TrimEnd('%');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int InferSubStatUpgrades(string rarity, string stat, object value, string rawName)
    {
        if (DomainStatRules.Value.SubStatValues.TryGetValue(rarity, out var rules))
        {
            var percent = value is string text && text.Trim().EndsWith('%');
            var keys = percent ? new[] { $"{stat}%", stat } : new[] { stat, $"{stat}%" };
            var observed = NumericValue(value);
            foreach (var key in keys)
            {
                if (observed is null || !rules.TryGetValue(key, out var range) || range.Step == 0) continue;
                var upgrades = (int)Math.Round((observed.Value - range.Start) / range.Step);
                var expected = range.Start + upgrades * range.Step;
                if (upgrades is >= 0 and <= 5 && Math.Abs(expected - observed.Value) <= 0.051)
                    return upgrades;
            }
        }

        return UpgradeRegex().Match(rawName) is { Success: true } match
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
    }

    private static string NormalizeSetName(string value) =>
        string.Concat(value.Split('[', '【')[0].Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static async Task<R4Catalog> LoadCatalogAsync()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Core.AppPaths.DataFile("soda-drive-disc-data.v1.json")));
        var root = document.RootElement;
        var sets = root.GetProperty("driveDiscSets").EnumerateArray().Select(set => new R4Set(
            set.GetProperty("id").GetString()!, set.GetProperty("name").GetString()!,
            set.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray())).ToList();
        var wikiSets = WikiData.Load().DiscCatalog.Sets;
        for (var index = 0; index < sets.Count; index++)
        {
            var formal = sets[index];
            var wiki = wikiSets.SingleOrDefault(set => string.Equals(set.Name, formal.Name, StringComparison.Ordinal));
            if (wiki is not null)
            {
                sets[index] = formal with
                {
                    Aliases = formal.Aliases
                        .Append(formal.Name)
                        .Concat(wiki.Aliases)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }
        }
        var supplemental = wikiSets
            .Where(set => string.Equals(set.Status, "candidate", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(set.StableId))
            .Select(set => new R4Set(set.StableId, set.Name,
                set.Aliases.Append(set.Name).Distinct(StringComparer.Ordinal).ToArray()));
        foreach (var candidate in supplemental)
        {
            if (sets.All(set => !string.Equals(set.Id, candidate.Id, StringComparison.Ordinal)))
                sets.Add(candidate);
        }
        var collisions = sets
            .SelectMany(set => set.Aliases.Append(set.Name).Select(alias => (Alias: NormalizeSetName(alias), set.Id)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Alias))
            .GroupBy(entry => entry.Alias, StringComparer.Ordinal)
            .Where(group => group.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToArray();
        if (collisions.Length > 0)
            throw new InvalidDataException($"r4_set_alias_collision:{string.Join(',', collisions)}");
        return new R4Catalog(
            root.GetProperty("gameVersion").GetString() ?? "unknown",
            root.GetProperty("dataVersion").GetString() ?? "unknown",
            sets);
    }

    private static string ContentHash(object value)
    {
        var node = JsonSerializer.SerializeToNode(value) ?? throw new InvalidDataException("r4_hash_input_invalid");
        var canonical = Canonicalize(node).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}";
    }

    internal static string ContentHashForContract(JsonNode value) => ContentHash(value);

    private static JsonNode Canonicalize(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Canonicalize(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(child => child is null ? null : Canonicalize(child)).ToArray()),
        _ => node.DeepClone()
    };

    private static Dictionary<string, object?> PayloadIdentityV2(Dictionary<string, object?> item) =>
        new()
        {
            ["id"] = item["id"],
            ["batchId"] = item["batchId"],
            ["sequence"] = item["sequence"],
            ["sourceIdentity"] = item["sourceIdentity"],
            ["duplicate"] = item["duplicate"],
            ["fingerprint"] = item["fingerprint"],
            ["lockState"] = item["lockState"],
            ["candidate"] = item["candidate"],
            ["fields"] = item["fields"],
            ["confirmations"] = item["confirmations"],
            ["issues"] = item["issues"],
            ["state"] = item["state"],
            ["evidence"] = item["evidence"]
        };

    private static JsonObject PayloadIdentityV2(JsonObject item) => new()
    {
        ["id"] = item["id"]?.DeepClone(),
        ["batchId"] = item["batchId"]?.DeepClone(),
        ["sequence"] = item["sequence"]?.DeepClone(),
        ["sourceIdentity"] = item["sourceIdentity"]?.DeepClone(),
        ["duplicate"] = item["duplicate"]?.DeepClone(),
        ["fingerprint"] = item["fingerprint"]?.DeepClone(),
        ["lockState"] = item["lockState"]?.DeepClone(),
        ["candidate"] = item["candidate"]?.DeepClone(),
        ["fields"] = item["fields"]?.DeepClone(),
        ["confirmations"] = item["confirmations"]?.DeepClone(),
        ["issues"] = item["issues"]?.DeepClone(),
        ["state"] = item["state"]?.DeepClone(),
        ["evidence"] = item["evidence"]?.DeepClone()
    };

    [GeneratedRegex(@"\+\s*([0-5])")]
    private static partial Regex UpgradeRegex();

    private sealed record R4Issue(string Field, string Code, string Message);
    private sealed record R4Catalog(string GameVersion, string DataVersion, IReadOnlyList<R4Set> Sets);
    private sealed record R4Set(string Id, string Name, IReadOnlyList<string> Aliases);
}
