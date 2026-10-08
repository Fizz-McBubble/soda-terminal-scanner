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
    private TemplateRoute ResolveTemplateRoute(string fieldKey, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        var requestedFamily = ProfileFamilyId(normalizedProfileId);
        var exactTemplates = TemplatesForFieldAndProfile(fieldKey, normalizedProfileId);
        if (exactTemplates.Count > 0)
        {
            return new TemplateRoute(
                exactTemplates,
                normalizedProfileId,
                "exact",
                requestedFamily,
                $"profile_exact:{normalizedProfileId}");
        }

        if (routingMode == ProfileRoutingMode.Strict)
        {
            return new TemplateRoute(
                [],
                normalizedProfileId,
                "strict_missing",
                requestedFamily,
                $"profile_strict_missing:{normalizedProfileId}");
        }

        if (!string.IsNullOrWhiteSpace(requestedFamily))
        {
            var familyTemplates = TemplatesForFieldAndFamily(fieldKey, requestedFamily);
            if (familyTemplates.Count > 0)
            {
                return new TemplateRoute(
                    familyTemplates,
                    requestedFamily,
                    "family",
                    requestedFamily,
                    $"profile_family:{normalizedProfileId}->{requestedFamily}");
            }
        }

        if (routingMode == ProfileRoutingMode.Family)
        {
            return new TemplateRoute(
                [],
                requestedFamily,
                "family_missing",
                requestedFamily,
                $"profile_family_missing:{normalizedProfileId}->{requestedFamily}");
        }

        var compatibleProfile = FindCompatibleProfile(normalizedProfileId, fieldKey);
        if (!string.IsNullOrWhiteSpace(compatibleProfile))
        {
            var compatibleTemplates = TemplatesForFieldAndProfile(fieldKey, compatibleProfile);
            if (compatibleTemplates.Count > 0)
            {
                return new TemplateRoute(
                    compatibleTemplates,
                    compatibleProfile,
                    "compatible",
                    ProfileFamilyId(compatibleProfile),
                    $"profile_compatible:{normalizedProfileId}->{compatibleProfile}");
            }
        }

        if (routingMode == ProfileRoutingMode.Auto)
        {
            var allTemplates = TemplatesForField(fieldKey);
            if (allTemplates.Count > 0)
            {
                return new TemplateRoute(
                    allTemplates,
                    "",
                    "global",
                    "",
                    $"profile_global_fallback:{normalizedProfileId}");
            }
        }

        return new TemplateRoute(
            [],
            normalizedProfileId,
            "no_templates",
            requestedFamily,
            $"profile_no_templates:{normalizedProfileId}");
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForFieldAndFamily(string fieldKey, string profileFamilyId)
    {
        _byFieldAndFamily ??= Templates
            .GroupBy(template => ProfilePolicyKey(ProfileFamilyId(template.VisualProfileId), template.FieldKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byFieldAndFamily.TryGetValue(ProfilePolicyKey(profileFamilyId, fieldKey), out var templates) ? templates : [];
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForFieldAndProfile(string fieldKey, string visualProfileId)
    {
        _byFieldAndProfile ??= Templates
            .GroupBy(template => ProfilePolicyKey(NormalizeProfileId(template.VisualProfileId), template.FieldKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byFieldAndProfile.TryGetValue(ProfilePolicyKey(visualProfileId, fieldKey), out var templates) ? templates : [];
    }

    private string FindCompatibleProfile(string visualProfileId, string fieldKey)
    {
        var requested = VisualProfileKey.Parse(visualProfileId);
        if (!requested.IsUsable)
        {
            return "";
        }

        var candidates = Templates
            .Where(template => template.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))
            .Select(template => NormalizeProfileId(template.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(profile => (profile, key: VisualProfileKey.Parse(profile)))
            .Where(item => item.key.IsUsable
                && item.key.ClientKind.Equals(requested.ClientKind, StringComparison.OrdinalIgnoreCase)
                && item.key.QualityLabel.Equals(requested.QualityLabel, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.profile, delta: Math.Abs(item.key.AspectRatio - requested.AspectRatio), pixels: Math.Abs(item.key.PixelCount - requested.PixelCount)))
            .OrderBy(item => item.delta)
            .ThenBy(item => item.pixels)
            .FirstOrDefault();
        return candidates.delta <= 0.03 ? candidates.profile : "";
    }

    private void NormalizeForUse()
    {
        Version = CurrentVersion;
        Feature = string.IsNullOrWhiteSpace(Feature) ? LegacyFeature : Feature;
        FieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(FieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        ProfileFieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(ProfileFieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        FamilyFieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(FamilyFieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var template in Templates)
        {
            template.VisualProfileId = NormalizeProfileId(template.VisualProfileId);
            template.ProfileFamilyId = string.IsNullOrWhiteSpace(template.ProfileFamilyId)
                ? ProfileFamilyId(template.VisualProfileId)
                : NormalizeProfileId(template.ProfileFamilyId);
        }

        foreach (var field in SupportedFields)
        {
            var templates = Templates
                .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var policy = FieldPolicies.TryGetValue(field, out var existing)
                ? existing
                : FastOcrFieldPolicy.Default(field);
            policy.TemplateCount = templates.Length;
            policy.LabelCount = templates
                .Select(template => template.Label)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            DisableNameAssist(field, policy);
            FieldPolicies[field] = policy;
        }

        foreach (var profileId in Templates
            .Select(template => NormalizeProfileId(template.VisualProfileId))
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in SupportedFields)
            {
                var key = ProfilePolicyKey(profileId, field);
                var templates = Templates
                    .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && NormalizeProfileId(template.VisualProfileId).Equals(profileId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!ProfileFieldPolicies.TryGetValue(key, out var policy))
                {
                    continue;
                }

                policy.TemplateCount = templates.Length;
                policy.LabelCount = templates
                    .Select(template => template.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                DisableNameAssist(field, policy);
                ProfileFieldPolicies[key] = policy;
            }
        }

        foreach (var familyId in Templates
            .Select(template => ProfileFamilyId(template.VisualProfileId))
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in SupportedFields)
            {
                var key = ProfilePolicyKey(familyId, field);
                var templates = Templates
                    .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && ProfileFamilyId(template.VisualProfileId).Equals(familyId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!FamilyFieldPolicies.TryGetValue(key, out var policy))
                {
                    continue;
                }

                policy.TemplateCount = templates.Length;
                policy.LabelCount = templates
                    .Select(template => template.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                DisableNameAssist(field, policy);
                FamilyFieldPolicies[key] = policy;
            }
        }

        _byField = null;
        _byFieldAndProfile = null;
        _byFieldAndFamily = null;
    }

    public static string NormalizeProfileId(string? visualProfileId)
    {
        return string.IsNullOrWhiteSpace(visualProfileId)
            ? "legacy"
            : visualProfileId.Trim().ToLowerInvariant();
    }

    public static string ProfilePolicyKey(string visualProfileId, string fieldKey)
    {
        return $"{NormalizeProfileId(visualProfileId)}|{fieldKey}";
    }

    private static void DisableNameAssist(string fieldKey, FastOcrFieldPolicy policy)
    {
        if (fieldKey.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            policy.AssistEnabled = false;
        }
    }

    private static string FieldFromPolicyKey(string policyKey)
    {
        var separator = policyKey.LastIndexOf('|');
        return separator >= 0 && separator + 1 < policyKey.Length
            ? policyKey[(separator + 1)..]
            : policyKey;
    }

    public static string ProfileFamilyId(string? visualProfileId)
    {
        var normalized = NormalizeProfileId(visualProfileId);
        var key = VisualProfileKey.Parse(normalized);
        if (!key.IsUsable)
        {
            return normalized.Equals("legacy", StringComparison.OrdinalIgnoreCase) ? "legacy" : "";
        }

        return $"{key.ClientKind}-{key.AspectBucket}-{key.DpiBucket}-{key.QualityLabel}".ToLowerInvariant();
    }

    private static double ScoreFromDistance(int distance, int bitCount)
    {
        return bitCount <= 0 ? 0 : 1.0 - distance / (double)bitCount;
    }
}

public sealed record FastOcrProfileRoute(
    string FieldKey,
    string RequestedProfileId,
    string PolicyProfileId,
    string RouteName,
    int TemplateCount,
    string ProfileFamilyId,
    string Reason);

internal sealed record TemplateRoute(
    IReadOnlyList<FastOcrTemplate> Templates,
    string PolicyProfileId,
    string RouteName,
    string ProfileFamilyId,
    string Reason);

internal readonly record struct VisualProfileKey(string ClientKind, int Width, int Height, string QualityLabel)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(ClientKind) && Width > 0 && Height > 0;

    public double AspectRatio => Height <= 0 ? 0 : Width / (double)Height;

    public string AspectBucket => Width <= 0 || Height <= 0
        ? "unknown"
        : NormalizeBucket(Math.Round(AspectRatio, 2).ToString("F2", CultureInfo.InvariantCulture));

    public string DpiBucket => "dpi";

    public int PixelCount => Math.Max(0, Width) * Math.Max(0, Height);

    public string ToFamilyId(string qualityLabel)
    {
        var normalizedQuality = string.IsNullOrWhiteSpace(qualityLabel)
            ? "current"
            : qualityLabel.Trim().ToLowerInvariant();
        return $"{ClientKind}-{AspectBucket}-{DpiBucket}-{normalizedQuality}";
    }

    public static VisualProfileKey Parse(string profileId)
    {
        var parts = profileId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return new VisualProfileKey("", 0, 0, "");
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var sizeParts = parts[i].Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (sizeParts.Length != 2
                || !int.TryParse(sizeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(sizeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            var quality = i + 1 < parts.Length
                ? string.Join("-", parts.Skip(i + 1))
                : "current";
            return new VisualProfileKey(parts[0], width, height, quality);
        }

        return new VisualProfileKey("", 0, 0, "");
    }

    private static string NormalizeBucket(string value)
    {
        return string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '-'))
            .Trim('-')
            .ToLowerInvariant();
    }
}

public sealed class FastOcrFieldPolicy
{
    public bool AssistEnabled { get; set; }
    public double MinScore { get; set; } = FastOcrTemplateIndex.DefaultMinScore;
    public double MinMargin { get; set; } = FastOcrTemplateIndex.DefaultMinMargin;
    public int TemplateCount { get; set; }
    public int LabelCount { get; set; }

    public static FastOcrFieldPolicy Default(string fieldKey)
    {
        return new FastOcrFieldPolicy
        {
            AssistEnabled = FastOcrTemplateIndex.IsDefaultAssistField(fieldKey),
            MinScore = FastOcrTemplateIndex.DefaultMinScore,
            MinMargin = FastOcrTemplateIndex.DefaultMinMargin
        };
    }
}

public sealed class FastOcrTemplate
{
    public string FieldKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string VisualProfileId { get; set; } = "legacy";
    public string ProfileFamilyId { get; set; } = "";
    public string[] Bits { get; set; } = [];
    public string SourceImage { get; set; } = "";
}

public sealed record FastOcrMatch(
    string FieldKey,
    string Label,
    double Score,
    int Distance,
    int CandidateCount,
    string SourceImage,
    string SourceProfileId,
    string SourceFamilyId,
    string Top2Label,
    double Top2Score,
    double Margin,
    bool AssistEnabled,
    double MinScore,
    double MinMargin,
    bool CanonicalCropSucceeded,
    bool CanonicalCropFallback,
    double FeatureElapsedMs,
    string Reason)
{
    public static FastOcrMatch Empty(string fieldKey, string reason) =>
        new(fieldKey, "", 0, FastOcrImageFeature.CurrentBitCount, 0, "", "", "", "", 0, 0, false, FastOcrTemplateIndex.DefaultMinScore, FastOcrTemplateIndex.DefaultMinMargin, false, false, 0, reason);
}

public sealed record FastOcrTemplateCoverage(
    string FieldKey,
    string RequestedProfileId,
    string PolicyProfileId,
    string RouteName,
    string ProfileFamilyId,
    int TemplateCount,
    int LabelCount,
    IReadOnlyList<string> MissingLabels,
    string Reason)
{
    public bool IsComplete => MissingLabels.Count == 0;
}
