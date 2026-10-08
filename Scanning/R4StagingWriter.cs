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

internal sealed record R4ScanRecord(
    int Sequence,
    DriveDiscExport? Export,
    IReadOnlyList<OcrResult> Ocr,
    string DetailPath,
    string VisualDetailHash,
    string CardPath,
    LockStateEvidence.Combined LockEvidence,
    DriveDiscParseDiagnostic? ParseDiagnostic = null);

internal static partial class R4StagingWriter
{
    private static readonly Lazy<StatRules> DomainStatRules = new(() => WikiData.Load().StatRules);

    private static readonly IReadOnlyDictionary<string, string> StatKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["生命值"] = "hp_flat",
            ["生命值%"] = "hp_percent",
            ["攻击力"] = "atk_flat",
            ["攻击力%"] = "atk_percent",
            ["防御力"] = "def_flat",
            ["防御力%"] = "def_percent",
            ["暴击率"] = "crit_rate",
            ["暴击伤害"] = "crit_dmg",
            ["异常精通"] = "anomaly_proficiency",
            ["穿透值"] = "pen",
            ["穿透率"] = "pen_ratio",
            ["冲击力"] = "impact",
            ["异常掌控"] = "anomaly_mastery",
            ["能量自动回复"] = "energy_regen",
            ["物理属性伤害加成"] = "physical_dmg",
            ["物理伤害加成"] = "physical_dmg",
            ["火属性伤害加成"] = "fire_dmg",
            ["冰属性伤害加成"] = "ice_dmg",
            ["电属性伤害加成"] = "electric_dmg",
            ["风属性伤害加成"] = "wind_dmg",
            ["以太属性伤害加成"] = "ether_dmg",
            ["以太伤害加成"] = "ether_dmg"
        };

    public static async Task<string> WriteAsync(
        string outputDirectory,
        int expectedTotal,
        string viewport,
        IReadOnlyList<R4ScanRecord> records)
    {
        var ordered = records.OrderBy(record => record.Sequence).ToArray();
        if (ordered.Length != expectedTotal
            || ordered.Select(record => record.Sequence).SequenceEqual(Enumerable.Range(1, expectedTotal)) == false)
        {
            throw new InvalidDataException("r4_sequence_or_total_mismatch");
        }

        var catalog = await LoadCatalogAsync();
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var batchId = $"scanner-r10c-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}";
        var items = ordered.Select(record => BuildItem(record, batchId, now, catalog)).ToArray();
        if (items.Select(item => (string)item["sourceIdentity"]!).Distinct(StringComparer.Ordinal).Count() != expectedTotal)
        {
            throw new InvalidDataException("r4_source_identity_duplicate");
        }

        var batch = new Dictionary<string, object?>
        {
            ["id"] = batchId,
            ["source"] = "ZZZ-Scanner.Next 1.0.49 Soda PP-OCRv6 direct",
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["dataVersion"] = catalog.DataVersion,
            ["recognitionVersion"] = "PaddleOCR-PP-OCRv6-small-rec-onnx+r10c-dotnet-r4-v2",
            ["sourceReport"] = "scan.log",
            ["total"] = expectedTotal,
            ["importHistory"] = Array.Empty<object>(),
            ["reviewState"] = new Dictionary<string, object?>
            {
                ["revision"] = 0,
                ["preflight"] = "stale",
                ["preflightRevision"] = null,
                ["armedRevision"] = null
            }
        };
        var payloadIdentity = items.Select(PayloadIdentityV2).ToArray();
        var sourceIdentity = new Dictionary<string, object?>
        {
            ["id"] = batchId,
            ["source"] = batch["source"],
            ["createdAt"] = now,
            ["dataVersion"] = catalog.DataVersion,
            ["recognitionVersion"] = batch["recognitionVersion"],
            ["sourceReport"] = "scan.log",
            ["total"] = expectedTotal,
            ["gameVersion"] = catalog.GameVersion,
            ["scanConfigIdentity"] = "zzz-scanner-next-1.0.49-r10c-ppocrv6",
            ["viewport"] = viewport,
            ["legacyAdapter"] = null
        };
        batch["manifest"] = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["batchId"] = batchId,
            ["expectedTotal"] = expectedTotal,
            ["capturedTotal"] = items.Length,
            ["uniqueItemCount"] = expectedTotal,
            ["gameVersion"] = catalog.GameVersion,
            ["scanConfigIdentity"] = "zzz-scanner-next-1.0.49-r10c-ppocrv6",
            ["viewport"] = viewport,
            ["payloadHash"] = ContentHash(payloadIdentity),
            ["sourceHash"] = ContentHash(sourceIdentity)
        };
        var staging = new Dictionary<string, object?>
        {
            ["format"] = "soda-terminal-scan-staging",
            ["formatVersion"] = 1,
            ["batch"] = batch,
            ["items"] = items
        };
        var file = Path.Combine(outputDirectory, "scanner-r10c-r4-staging.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(staging, JsonDefaults.Write));
        return file;
    }

    internal static async Task RebuildManifestFromStagingAsync(string inputFile, string outputFile)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(inputFile))?.AsObject()
            ?? throw new InvalidDataException("r4_staging_root_invalid");
        var batch = root["batch"]?.AsObject()
            ?? throw new InvalidDataException("r4_staging_batch_invalid");
        var manifest = batch["manifest"]?.AsObject()
            ?? throw new InvalidDataException("r4_staging_manifest_invalid");
        var items = root["items"]?.AsArray()
            ?? throw new InvalidDataException("r4_staging_items_invalid");
        var ordered = items
            .Select(item => item?.AsObject() ?? throw new InvalidDataException("r4_staging_item_invalid"))
            .OrderBy(item => item["sequence"]?.GetValue<int>()
                ?? throw new InvalidDataException("r4_staging_sequence_invalid"))
            .ToArray();

        var payloadIdentity = new JsonArray(ordered.Select(PayloadIdentityV2).ToArray());
        var sourceIdentity = new JsonObject
        {
            ["id"] = batch["id"]?.DeepClone(),
            ["source"] = batch["source"]?.DeepClone(),
            ["createdAt"] = batch["createdAt"]?.DeepClone(),
            ["dataVersion"] = batch["dataVersion"]?.DeepClone(),
            ["recognitionVersion"] = batch["recognitionVersion"]?.DeepClone(),
            ["sourceReport"] = batch["sourceReport"]?.DeepClone(),
            ["total"] = batch["total"]?.DeepClone(),
            ["gameVersion"] = manifest["gameVersion"]?.DeepClone(),
            ["scanConfigIdentity"] = manifest["scanConfigIdentity"]?.DeepClone(),
            ["viewport"] = manifest["viewport"]?.DeepClone(),
            ["legacyAdapter"] = manifest["legacyAdapter"]?.DeepClone()
        };

        manifest["schemaVersion"] = 2;
        manifest["payloadHash"] = ContentHash(payloadIdentity);
        manifest["sourceHash"] = ContentHash(sourceIdentity);
        await File.WriteAllTextAsync(outputFile, root.ToJsonString(JsonDefaults.Write));
    }

    internal static async Task ReconcileReviewStateAsync(string inputFile, string outputFile)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(inputFile))?.AsObject()
            ?? throw new InvalidDataException("r4_staging_root_invalid");
        var items = root["items"]?.AsArray()
            ?? throw new InvalidDataException("r4_staging_items_invalid");

        foreach (var item in items)
        {
            var record = item?.AsObject() ?? throw new InvalidDataException("r4_staging_item_invalid");
            var issues = record["issues"]?.AsArray()
                ?? throw new InvalidDataException("r4_staging_issues_invalid");
            var retained = issues
                .Where(issue => !string.Equals(issue?["code"]?.GetValue<string>(), "low_confidence", StringComparison.Ordinal))
                .Select(issue => issue?.DeepClone())
                .ToArray();
            var state = StateForIssues(
                record["state"]?.GetValue<string>() ?? "needs_review",
                retained.Select(issue => (
                    Code: issue?["code"]?.GetValue<string>() ?? "unknown",
                    Severity: issue?["severity"]?.GetValue<string>() ?? "review")));
            record["issues"] = new JsonArray(retained);
            record["state"] = state;
        }

        await File.WriteAllTextAsync(outputFile, root.ToJsonString(JsonDefaults.Write));
    }

    private static Dictionary<string, object?> BuildItem(
        R4ScanRecord record,
        string batchId,
        string updatedAt,
        R4Catalog catalog)
    {
        var export = record.Export;
        var rawSet = record.Ocr.ElementAtOrDefault(0);
        var normalizedSet = NormalizeSetName(rawSet.Text);
        var set = catalog.Sets.SingleOrDefault(candidate =>
            candidate.Aliases.Any(alias => NormalizeSetName(alias) == normalizedSet));
        var main = export?.MainStat.SingleOrDefault();
        var mainKey = main is null ? null : MapStat(main.Value.Key, main.Value.Value);
        var parserRoot = RootIssueFor(record.ParseDiagnostic);
        var rootIssues = new List<R4Issue>();
        var cascadingIssues = new List<R4Issue>();
        if (parserRoot is not null) rootIssues.Add(parserRoot);
        if (set is null) rootIssues.Add(new R4Issue("setId", "missing_set", "套装名无法精确映射。"));
        if (mainKey is null)
        {
            (parserRoot is not null ? cascadingIssues : rootIssues).Add(
                new R4Issue("mainStat", "missing_main_stat", "主词条无法精确映射。"));
        }
        // Game-side locking is retained as source evidence only. Soda Terminal never uses it
        // to gate review, import, or player-facing product decisions.

        var subStats = new List<Dictionary<string, object?>>();
        var subMappingIssues = new List<R4Issue>();
        for (var index = 0; index < (export?.SubStats.Count ?? 0); index++)
        {
            var pair = export!.SubStats[index].Single();
            var rawName = record.Ocr.ElementAtOrDefault(4 + index * 2).Text;
            var rawValue = record.Ocr.ElementAtOrDefault(5 + index * 2).Text;
            var stat = MapStat(pair.Key, pair.Value);
            var upgrades = InferSubStatUpgrades(export.Rarity, pair.Key, pair.Value, rawName);
            if (stat is null)
                subMappingIssues.Add(new R4Issue($"subStats.{index}", "missing_sub_stat", $"副词条 {index + 1} 无法精确映射。"));
            subStats.Add(new Dictionary<string, object?>
            {
                ["stat"] = stat,
                ["value"] = NumericValue(pair.Value),
                ["upgrades"] = upgrades,
                ["rawText"] = $"{rawName} {rawValue}".Trim(),
                ["confidence"] = record.Ocr.ElementAtOrDefault(4 + index * 2).Score >= 0.75f ? "high" : "low"
            });
        }
        var expectedSubStats = export is not null && export.Level < 3 ? 3 : 4;
        if (subStats.Count < expectedSubStats)
        {
            foreach (var missingIndex in Enumerable.Range(subStats.Count, expectedSubStats - subStats.Count))
            {
                if (parserRoot?.Field == $"subStats.{missingIndex}") continue;
                (parserRoot is not null ? cascadingIssues : rootIssues).Add(
                    new R4Issue($"subStats.{missingIndex}", "missing_sub_stat", $"缺少第 {missingIndex + 1} 条副词条。"));
            }
        }
        foreach (var issue in subMappingIssues)
            (parserRoot is not null ? cascadingIssues : rootIssues).Add(issue);

        var issues = CombineRootAndCascadingIssues(rootIssues, cascadingIssues);
        var inferredSlot = export?.Slot ?? record.ParseDiagnostic?.Slot;
        var parserEvidence = ParserEvidence(record.ParseDiagnostic, parserRoot);

        var sourceIdentity = $"sha256:{record.VisualDetailHash}:{record.Sequence:D4}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity))).ToLowerInvariant()[..24];
        var fields = new Dictionary<string, object?>
        {
            ["setName"] = Field(rawSet.Text, set?.Name, set is null ? "low" : "high", "exact_versioned_catalog_alias"),
            ["slot"] = Field(inferredSlot?.ToString(CultureInfo.InvariantCulture) ?? "", inferredSlot, inferredSlot is null ? "low" : "high", inferredSlot == export?.Slot ? "upstream_slot_pattern" : "ppocrv6_domain_diagnostic"),
            ["level"] = Field(record.Ocr.ElementAtOrDefault(1).Text, export?.Level, export is null ? "low" : "high", "rarity_level_pattern"),
            ["mainStat"] = Field(record.Ocr.ElementAtOrDefault(2).Text, mainKey, mainKey is null ? "low" : "high", "exact_stat_catalog", parserRoot?.Field == "mainStat" ? parserEvidence : null),
            ["lockState"] = Field(
                $"card={record.LockEvidence.Card.Ratio:F4};detail={record.LockEvidence.Detail.Ratio:F4}",
                record.LockEvidence.Value is null ? "unknown" : record.LockEvidence.Value,
                record.LockEvidence.IsKnown ? "high" : "low",
                record.LockEvidence.Gate,
                [
                    $"card={record.LockEvidence.Card.Gate}",
                    $"detail={record.LockEvidence.Detail.Gate}"
                ],
                "card-bottom-right+detail-lock-button")
        };
        for (var index = 0; index < subStats.Count; index++)
            fields[$"subStats.{index}"] = Field((string)subStats[index]["rawText"]!, subStats[index]["stat"],
                (string)subStats[index]["confidence"]!, "exact_stat_catalog_plus_upgrade_marker");
        if (parserRoot?.Field.StartsWith("subStats.", StringComparison.Ordinal) == true)
        {
            foreach (var missingPosition in record.ParseDiagnostic?.MissingPositions ?? Array.Empty<int>())
                fields[$"subStats.{missingPosition}"] = Field(
                    "",
                    null,
                    "low",
                    "ppocrv6_domain_diagnostic",
                    parserEvidence);
        }

        return new Dictionary<string, object?>
        {
            ["id"] = $"{batchId}:{sourceIdentity}",
            ["batchId"] = batchId,
            ["sequence"] = record.Sequence,
            ["sourceIdentity"] = sourceIdentity,
            ["state"] = StateForIssues("ready", issues.Select(issue => (
                Code: (string)issue["code"]!, Severity: (string)issue["severity"]!))),
            ["duplicate"] = false,
            ["fingerprint"] = fingerprint,
            ["lockState"] = record.LockEvidence.Value is null ? "unknown" : record.LockEvidence.Value,
            ["candidate"] = new Dictionary<string, object?>
            {
                ["setId"] = set?.Id,
                ["setName"] = set?.Name,
                ["slot"] = inferredSlot,
                ["level"] = export?.Level,
                ["rarity"] = export?.Rarity,
                ["mainStat"] = mainKey,
                ["mainStatValue"] = main is null ? null : NumericValue(main.Value.Value),
                ["subStats"] = subStats
            },
            ["fields"] = fields,
            ["confirmations"] = Array.Empty<object>(),
            ["issues"] = issues,
            ["evidence"] = new Dictionary<string, object?>
            {
                ["detailPath"] = record.DetailPath,
                ["cardPath"] = record.CardPath,
                ["visualDetailHash"] = $"sha256:{record.VisualDetailHash}",
                ["rawText"] = record.Ocr.Select((result, index) => new KeyValuePair<string, string>($"roi{index:D2}", result.Text))
                    .ToDictionary(pair => pair.Key, pair => pair.Value)
            },
            ["updatedAt"] = updatedAt
        };
    }

    private static Dictionary<string, object?> Field(
        string raw,
        object? value,
        string confidence,
        string rule,
        IReadOnlyList<string>? evidence = null,
        string source = "upstream-detail-roi") =>
        new()
        {
            ["rawText"] = raw,
            ["normalizedValue"] = value,
            ["confidence"] = confidence,
            ["evidence"] = evidence ?? Array.Empty<string>(),
            ["rule"] = rule,
            ["source"] = source
        };

    internal static string StateForIssues(
        string currentState,
        IEnumerable<(string Code, string Severity)> issues)
    {
        var blocking = issues
            .Where(issue => !string.Equals(issue.Code, "low_confidence", StringComparison.Ordinal))
            .ToArray();
        if (blocking.Any(issue => string.Equals(issue.Severity, "invalid", StringComparison.Ordinal)))
            return "invalid";
        if (blocking.Length > 0)
            return "needs_review";
        return currentState == "invalid" ? "invalid" : "ready";
    }

    private static Dictionary<string, object?> Issue(string field, string code, string message) => new()
    {
        ["field"] = field,
        ["code"] = code,
        ["message"] = message,
        ["severity"] = "review"
    };

    private static IReadOnlyList<Dictionary<string, object?>> CombineRootAndCascadingIssues(
        IReadOnlyList<R4Issue> roots,
        IReadOnlyList<R4Issue> cascading)
    {
        if (roots.Count == 1)
        {
            return [
                Issue(roots[0].Field, roots[0].Code, roots[0].Message),
                .. cascading.Select(issue => Issue(
                    issue.Field,
                    $"cascading_{issue.Code}",
                    $"由 {roots[0].Code} 连带：{issue.Message}"))
            ];
        }

        if (roots.Count == 0)
            return cascading.Select(issue => Issue(issue.Field, issue.Code, issue.Message)).ToArray();

        return [
            Issue("review", "review_root_conflict", "多个独立复核根因冲突，需人工确认。"),
            .. roots.Select(issue => Issue(
                issue.Field,
                $"cascading_{issue.Code}",
                $"由 review_root_conflict 连带：{issue.Message}")),
            .. cascading.Select(issue => Issue(
                issue.Field,
                $"cascading_{issue.Code}",
                $"由 review_root_conflict 连带：{issue.Message}"))
        ];
    }

    private static R4Issue? RootIssueFor(DriveDiscParseDiagnostic? diagnostic)
    {
        if (diagnostic is null) return null;
        if (diagnostic.Code == "substat_domain_incomplete")
        {
            var firstMissing = diagnostic.MissingPositions.FirstOrDefault(diagnostic.ObservedCount ?? 0);
            return new R4Issue(
                $"subStats.{firstMissing}",
                "substats_incomplete",
                $"副词条领域解析不足：{diagnostic.ObservedCount}/{diagnostic.RequiredCount}。");
        }

        if (diagnostic.Code == "stat_domain_ambiguous" && diagnostic.FieldKind == "main")
        {
            return diagnostic.Slot switch
            {
                4 => new R4Issue("mainStat", "slot_iv_main", "IV号位主词条无法在合法域内唯一确定。"),
                6 => new R4Issue("mainStat", "slot_vi_main", "VI号位主词条无法在合法域内唯一确定。"),
                5 when diagnostic.Candidates.Count(IsElementalDamageStat) >= 2
                    => new R4Issue("mainStat", "elemental_main", "V号位元素伤害主词条无法在合法元素域内唯一确定。"),
                _ => new R4Issue("mainStat", "parser_root_unknown", "主词条领域诊断无法唯一归入产品复核根因。")
            };
        }

        return new R4Issue("review", "parser_root_unknown", "解析诊断无法唯一归入产品复核根因。");
    }
}
