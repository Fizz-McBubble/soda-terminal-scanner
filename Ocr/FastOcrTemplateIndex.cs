using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ZZZScannerNext.Core;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public sealed partial class FastOcrTemplateIndex
{
    public const string CurrentVersion = "6";
    public const string LegacyFeature = "ahash-16x16-grayscale-v1";
    public const string CurrentFeature = "ahash-dhash-16x16-v3";
    public const string ExperimentalFeature = "ahash-dhash-vhash-16x16-v4";
    public const string CanonicalFeature = "canonical-ahash-dhash-vhash-edge-16x16-v6";
    public const double DefaultMinScore = 0.90;
    public const double DefaultMinMargin = 0.02;

    public string Version { get; set; } = CurrentVersion;
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    public string Feature { get; set; } = CurrentFeature;
    public List<FastOcrTemplate> Templates { get; set; } = new();
    public Dictionary<string, FastOcrFieldPolicy> FieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FastOcrFieldPolicy> ProfileFieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FastOcrFieldPolicy> FamilyFieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<FastOcrTemplate>>? _byField;
    private Dictionary<string, List<FastOcrTemplate>>? _byFieldAndProfile;
    private Dictionary<string, List<FastOcrTemplate>>? _byFieldAndFamily;

    public static IReadOnlySet<string> SupportedFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "level",
        "mainStat",
        "subStat1",
        "subStat2",
        "subStat3",
        "subStat4"
    };

    public static string DefaultIndexFile => AppPaths.DataFile("ocr_fast_templates.json");

    public static bool IsSupportedField(string fieldKey)
    {
        return SupportedFields.Contains(fieldKey);
    }

    public static bool IsDefaultAssistField(string fieldKey)
    {
        return IsSupportedField(fieldKey) && !fieldKey.Equals("name", StringComparison.OrdinalIgnoreCase);
    }

    public static FastOcrTemplateIndex Load(string file)
    {
        var index = JsonSerializer.Deserialize<FastOcrTemplateIndex>(File.ReadAllText(file), JsonDefaults.Read)
            ?? throw new InvalidDataException($"Cannot load fast OCR template index: {file}");
        index.NormalizeForUse();
        return index;
    }

    public void Save(string file)
    {
        NormalizeForUse();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
        File.WriteAllText(file, JsonSerializer.Serialize(this, JsonDefaults.Write));
    }

    public static bool TryValidateFastModeIndex(string? file, out string resolvedFile, out string reason)
    {
        resolvedFile = string.IsNullOrWhiteSpace(file)
            ? DefaultIndexFile
            : Path.GetFullPath(file);
        reason = "";

        if (!File.Exists(resolvedFile))
        {
            reason = $"fast OCR index not found: {resolvedFile}";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resolvedFile));
            var root = document.RootElement;
            var versionText = root.TryGetProperty("Version", out var versionElement)
                ? versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetRawText()
                    : versionElement.GetString()
                : "";
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                || version < 3)
            {
                reason = $"fast OCR index must be v3 or newer; found Version={versionText}";
                return false;
            }

            var index = Load(resolvedFile);
            if (!string.Equals(index.Feature, CurrentFeature, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(index.Feature, ExperimentalFeature, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(index.Feature, CanonicalFeature, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"unsupported fast OCR feature: {index.Feature}";
                return false;
            }

            if (index.Templates.Count == 0)
            {
                reason = "fast OCR index has no templates";
                return false;
            }

            var enabledFields = index.FieldPolicies
                .Concat(index.ProfileFieldPolicies.Select(pair => new KeyValuePair<string, FastOcrFieldPolicy>(FieldFromPolicyKey(pair.Key), pair.Value)))
                .Concat(index.FamilyFieldPolicies.Select(pair => new KeyValuePair<string, FastOcrFieldPolicy>(FieldFromPolicyKey(pair.Key), pair.Value)))
                .Where(pair => pair.Value.AssistEnabled && pair.Value.TemplateCount > 0)
                .Select(pair => pair.Key)
                .Where(IsDefaultAssistField)
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (enabledFields.Length == 0)
            {
                reason = "fast OCR index has no assist-enabled fields";
                return false;
            }

            reason = $"enabled_fields={string.Join("|", enabledFields)}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"cannot validate fast OCR index: {ex.Message}";
            return false;
        }
    }

    public static FastOcrTemplateIndex Build(IEnumerable<OcrShadowDatasetRow> rows, Action<string>? log = null, string featureName = CurrentFeature)
    {
        var index = new FastOcrTemplateIndex
        {
            Feature = string.IsNullOrWhiteSpace(featureName) ? CurrentFeature : featureName
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!IsSupportedField(row.FieldKey) || string.IsNullOrWhiteSpace(row.CleanLabel))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))
            {
                log?.Invoke($"skip_missing_image field={row.FieldKey} item={row.ItemIndex} image={row.ResolvedImageFile}");
                continue;
            }

            try
            {
                using var bitmap = new Bitmap(row.ResolvedImageFile);
                var feature = FastOcrImageFeature.FromBitmap(bitmap, index.Feature);
                var visualProfileId = NormalizeProfileId(row.VisualProfileId);
                var familyId = ProfileFamilyId(visualProfileId);
                var key = $"{visualProfileId}\0{row.FieldKey}\0{row.CleanLabel}\0{feature.ToKey()}";
                if (!seen.Add(key))
                {
                    continue;
                }

                index.Templates.Add(new FastOcrTemplate
                {
                    FieldKey = row.FieldKey,
                    Label = row.CleanLabel,
                    VisualProfileId = visualProfileId,
                    ProfileFamilyId = familyId,
                    Bits = feature.ToHexWords(),
                    SourceImage = Path.GetFullPath(row.ResolvedImageFile)
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"skip_bad_image field={row.FieldKey} item={row.ItemIndex} image={row.ResolvedImageFile} error={ex.Message}");
            }
        }

        index.Templates = index.Templates
            .OrderBy(template => template.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.VisualProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => string.Join("", template.Bits), StringComparer.Ordinal)
            .ToList();
        index.NormalizeForUse();
        return index;
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi)
    {
        return Match(fieldKey, source, roi, "");
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi, string visualProfileId)
    {
        return Match(fieldKey, source, roi, visualProfileId, ProfileRoutingMode.Auto);
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        var route = ResolveTemplateRoute(fieldKey, normalizedProfileId, routingMode);
        var candidates = route.Templates;
        if (candidates.Count == 0)
        {
            return FastOcrMatch.Empty(fieldKey, route.Reason);
        }

        var feature = FastOcrImageFeature.FromBitmap(source, roi, Feature);
        FastOcrTemplate? best = null;
        var bestDistance = int.MaxValue;
        FastOcrTemplate? secondDifferentLabel = null;
        var secondDifferentDistance = int.MaxValue;

        foreach (var template in candidates)
        {
            var distance = feature.DistanceTo(FastOcrImageFeature.FromHexWords(template.Bits));
            if (distance < bestDistance)
            {
                if (best is not null && !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase))
                {
                    secondDifferentLabel = best;
                    secondDifferentDistance = bestDistance;
                }

                best = template;
                bestDistance = distance;
                continue;
            }

            if (best is not null
                && !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase)
                && distance < secondDifferentDistance)
            {
                secondDifferentLabel = template;
                secondDifferentDistance = distance;
            }
        }

        if (best is null)
        {
            return FastOcrMatch.Empty(fieldKey, "no_match");
        }

        if (secondDifferentLabel is null)
        {
            foreach (var template in candidates.Where(template => !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase)))
            {
                var distance = feature.DistanceTo(FastOcrImageFeature.FromHexWords(template.Bits));
                if (distance < secondDifferentDistance)
                {
                    secondDifferentLabel = template;
                    secondDifferentDistance = distance;
                }
            }
        }

        var score = ScoreFromDistance(bestDistance, feature.BitCount);
        var top2Score = secondDifferentLabel is null ? 0 : ScoreFromDistance(secondDifferentDistance, feature.BitCount);
        var margin = secondDifferentLabel is null ? 1 : score - top2Score;
        var policy = PolicyForField(fieldKey, route.PolicyProfileId);
        return new FastOcrMatch(
            fieldKey,
            best.Label,
            score,
            bestDistance,
            candidates.Count,
            best.SourceImage,
            best.VisualProfileId,
            ProfileFamilyId(best.VisualProfileId),
            secondDifferentLabel?.Label ?? "",
            top2Score,
            margin,
            policy.AssistEnabled,
            policy.MinScore,
            policy.MinMargin,
            feature.CanonicalCropSucceeded,
            feature.CanonicalCropFallback,
            feature.FeatureElapsedMs,
            route.Reason);
    }

    public bool IsMatchAccepted(FastOcrMatch match, bool requireAssistEnabled)
    {
        if (string.IsNullOrWhiteSpace(match.Label))
        {
            return false;
        }

        if (requireAssistEnabled && !match.AssistEnabled)
        {
            return false;
        }

        return match.Score >= match.MinScore && match.Margin >= match.MinMargin;
    }

    public FastOcrFieldPolicy PolicyForField(string fieldKey)
    {
        return PolicyForField(fieldKey, "");
    }

    public FastOcrFieldPolicy PolicyForField(string fieldKey, string visualProfileId)
    {
        if (FieldPolicies is null || FieldPolicies.Count == 0)
        {
            NormalizeForUse();
        }

        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        if (!string.IsNullOrWhiteSpace(normalizedProfileId)
            && ProfileFieldPolicies.TryGetValue(ProfilePolicyKey(normalizedProfileId, fieldKey), out var profilePolicy))
        {
            DisableNameAssist(fieldKey, profilePolicy);
            return profilePolicy;
        }

        var familyId = ProfileFamilyId(normalizedProfileId);
        if (!string.IsNullOrWhiteSpace(familyId)
            && FamilyFieldPolicies.TryGetValue(ProfilePolicyKey(familyId, fieldKey), out var familyPolicy))
        {
            DisableNameAssist(fieldKey, familyPolicy);
            return familyPolicy;
        }

        var policies = FieldPolicies ?? new Dictionary<string, FastOcrFieldPolicy>(StringComparer.OrdinalIgnoreCase);
        var selectedPolicy = policies.TryGetValue(fieldKey, out var policy)
            ? policy
            : FastOcrFieldPolicy.Default(fieldKey);
        DisableNameAssist(fieldKey, selectedPolicy);
        return selectedPolicy;
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForField(string fieldKey)
    {
        _byField ??= Templates
            .GroupBy(template => template.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byField.TryGetValue(fieldKey, out var templates) ? templates : [];
    }

    public FastOcrProfileRoute DescribeRoute(string fieldKey, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var route = ResolveTemplateRoute(fieldKey, NormalizeProfileId(visualProfileId), routingMode);
        return new FastOcrProfileRoute(
            fieldKey,
            NormalizeProfileId(visualProfileId),
            route.PolicyProfileId,
            route.RouteName,
            route.Templates.Count,
            route.ProfileFamilyId,
            route.Reason);
    }

    public FastOcrTemplateCoverage DescribeLabelCoverage(
        string fieldKey,
        string visualProfileId,
        ProfileRoutingMode routingMode,
        IReadOnlyCollection<string> requiredLabels)
    {
        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        var route = ResolveTemplateRoute(fieldKey, normalizedProfileId, routingMode);
        var availableLabels = route.Templates
            .Select(template => template.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLabels = requiredLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(label => !availableLabels.Contains(label))
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FastOcrTemplateCoverage(
            fieldKey,
            normalizedProfileId,
            route.PolicyProfileId,
            route.RouteName,
            route.ProfileFamilyId,
            route.Templates.Count,
            availableLabels.Count,
            missingLabels,
            route.Reason);
    }
}
