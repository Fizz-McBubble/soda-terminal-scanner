using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public static partial class FastOcrEvaluator
{
    private static void WriteReport(string outputFile, IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("fold,csv_file,item_index,roi_index,visual_profile_id,profile_family_id,field_key,clean_label,fast_label,score,top2_label,top2_score,margin,assist_enabled,accepted,matches_clean,false_accept,elapsed_ms,source_family_id,canonical_crop_succeeded,canonical_crop_fallback,feature_ms,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Fold),
                EscapeCsv(row.CsvFile),
                row.ItemIndex.ToString(CultureInfo.InvariantCulture),
                row.RoiIndex.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.VisualProfileId),
                EscapeCsv(row.ProfileFamilyId),
                EscapeCsv(row.FieldKey),
                EscapeCsv(row.CleanLabel),
                EscapeCsv(row.FastLabel),
                row.Score.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Top2Label),
                row.Top2Score.ToString("F6", CultureInfo.InvariantCulture),
                row.Margin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.MatchesClean.ToString(CultureInfo.InvariantCulture),
                row.FalseAccept.ToString(CultureInfo.InvariantCulture),
                row.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                EscapeCsv(row.SourceFamilyId),
                row.CanonicalCropSucceeded.ToString(CultureInfo.InvariantCulture),
                row.CanonicalCropFallback.ToString(CultureInfo.InvariantCulture),
                row.FeatureMs.ToString("F3", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteConfusionReport(string outputFile, IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("kind,field_key,clean_label,fast_label,count,avg_score,avg_margin,folds");

        var falseAccepts = rows
            .Where(row => row.FalseAccept)
            .GroupBy(ConfusionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => FastOcrConfusionRow.From("false_accept", group))
            .ToArray();
        var rejects = rows
            .Where(row => !row.Accepted)
            .GroupBy(ConfusionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => FastOcrConfusionRow.From("reject", group))
            .ToArray();

        foreach (var row in falseAccepts.Concat(rejects)
            .OrderBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.Count)
            .ThenBy(row => row.CleanLabel, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Kind),
                EscapeCsv(row.FieldKey),
                EscapeCsv(row.CleanLabel),
                EscapeCsv(row.FastLabel),
                row.Count.ToString(CultureInfo.InvariantCulture),
                row.AvgScore.ToString("F6", CultureInfo.InvariantCulture),
                row.AvgMargin.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Folds)
            ]));
        }
    }

    private static IReadOnlyList<FastOcrCalibrationRow> CalibratePolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrCalibrationRow>();
        foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
        {
            var values = evaluations
                .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var selected = SelectThreshold(values);
            var policy = index.PolicyForField(field);
            var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                ? NameAssistMinAcceptRate
                : DefaultAssistMinAcceptRate;
            var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
            var enabled = hasCrossValidation
                && eligibleField
                && selected.FalseAccepts == 0
                && selected.Accepted > 0
                && selected.AcceptRate >= minAcceptRate;
            var reason = enabled
                ? ""
                : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);

            policy.AssistEnabled = enabled;
            policy.MinScore = selected.MinScore;
            policy.MinMargin = selected.MinMargin;
            index.FieldPolicies[field] = policy;

            rows.Add(new FastOcrCalibrationRow(
                field,
                values.Length,
                selected.Accepted,
                selected.FalseAccepts,
                selected.AcceptRate,
                selected.MatchRate,
                selected.MinScore,
                selected.MinMargin,
                enabled,
                minAcceptRate,
                reason));
        }

        return rows;
    }

    private static IReadOnlyList<FastOcrProfileCalibrationRow> CalibrateProfilePolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrProfileCalibrationRow>();
        foreach (var profileGroup in evaluations
            .GroupBy(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
            {
                var values = profileGroup
                    .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selected = SelectThreshold(values);
                var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? NameAssistMinAcceptRate
                    : DefaultAssistMinAcceptRate;
                var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
                var enabled = hasCrossValidation
                    && eligibleField
                    && selected.FalseAccepts == 0
                    && selected.Accepted > 0
                    && selected.AcceptRate >= minAcceptRate;
                var reason = enabled
                    ? ""
                    : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);
                var policy = index.PolicyForField(field);
                var profilePolicy = new FastOcrFieldPolicy
                {
                    AssistEnabled = enabled,
                    MinScore = selected.MinScore,
                    MinMargin = selected.MinMargin,
                    TemplateCount = index.Templates.Count(template =>
                        template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && FastOcrTemplateIndex.NormalizeProfileId(template.VisualProfileId).Equals(profileGroup.Key, StringComparison.OrdinalIgnoreCase)),
                    LabelCount = index.Templates
                        .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                            && FastOcrTemplateIndex.NormalizeProfileId(template.VisualProfileId).Equals(profileGroup.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(template => template.Label)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                };

                if (profilePolicy.TemplateCount == 0)
                {
                    profilePolicy.TemplateCount = policy.TemplateCount;
                    profilePolicy.LabelCount = policy.LabelCount;
                }

                index.ProfileFieldPolicies[FastOcrTemplateIndex.ProfilePolicyKey(profileGroup.Key, field)] = profilePolicy;
                rows.Add(new FastOcrProfileCalibrationRow(
                    profileGroup.Key,
                    field,
                    values.Length,
                    selected.Accepted,
                    selected.FalseAccepts,
                    selected.AcceptRate,
                    selected.MatchRate,
                    selected.MinScore,
                    selected.MinMargin,
                    enabled,
                    minAcceptRate,
                    reason));
            }
        }

        return rows;
    }

    private static IReadOnlyList<FastOcrFamilyCalibrationRow> CalibrateFamilyPolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrFamilyCalibrationRow>();
        foreach (var familyGroup in evaluations
            .GroupBy(row => string.IsNullOrWhiteSpace(row.ProfileFamilyId) ? "unknown" : row.ProfileFamilyId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
            {
                var values = familyGroup
                    .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selected = SelectThreshold(values);
                var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? NameAssistMinAcceptRate
                    : DefaultAssistMinAcceptRate;
                var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
                var enabled = hasCrossValidation
                    && eligibleField
                    && selected.FalseAccepts == 0
                    && selected.Accepted > 0
                    && selected.AcceptRate >= minAcceptRate;
                var reason = enabled
                    ? ""
                    : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);

                var familyPolicy = new FastOcrFieldPolicy
                {
                    AssistEnabled = enabled,
                    MinScore = selected.MinScore,
                    MinMargin = selected.MinMargin,
                    TemplateCount = index.Templates.Count(template =>
                        template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && FastOcrTemplateIndex.ProfileFamilyId(template.VisualProfileId).Equals(familyGroup.Key, StringComparison.OrdinalIgnoreCase)),
                    LabelCount = index.Templates
                        .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                            && FastOcrTemplateIndex.ProfileFamilyId(template.VisualProfileId).Equals(familyGroup.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(template => template.Label)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                };

                index.FamilyFieldPolicies[FastOcrTemplateIndex.ProfilePolicyKey(familyGroup.Key, field)] = familyPolicy;
                rows.Add(new FastOcrFamilyCalibrationRow(
                    familyGroup.Key,
                    field,
                    values.Length,
                    selected.Accepted,
                    selected.FalseAccepts,
                    selected.AcceptRate,
                    selected.MatchRate,
                    selected.MinScore,
                    selected.MinMargin,
                    enabled,
                    minAcceptRate,
                    reason));
            }
        }

        return rows;
    }

    private static string CalibrationDisableReason(
        bool hasCrossValidation,
        bool eligibleField,
        FastOcrThresholdChoice selected,
        double minAcceptRate)
    {
        if (!hasCrossValidation)
        {
            return "needs_at_least_two_shadow_runs";
        }

        if (!eligibleField)
        {
            return "field_not_assist_eligible";
        }

        if (selected.FalseAccepts > 0)
        {
            return "false_accepts";
        }

        if (selected.Accepted == 0)
        {
            return "no_safe_accepts";
        }

        if (selected.AcceptRate < minAcceptRate)
        {
            return $"accept_rate_below_{minAcceptRate.ToString("F2", CultureInfo.InvariantCulture)}";
        }

        return "disabled";
    }

    private static FastOcrThresholdChoice SelectThreshold(IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        FastOcrThresholdChoice? best = null;
        foreach (var minScore in ScoreCandidates)
        {
            foreach (var minMargin in MarginCandidates)
            {
                var accepted = rows
                    .Where(row => IsCandidateAccepted(row, minScore, minMargin))
                    .ToArray();
                var falseAccepts = accepted.Count(row => !LabelMatches(row));
                if (falseAccepts > 0)
                {
                    continue;
                }

                var matches = accepted.Count(LabelMatches);
                var choice = new FastOcrThresholdChoice(
                    minScore,
                    minMargin,
                    accepted.Length,
                    falseAccepts,
                    rows.Count == 0 ? 0 : accepted.Length / (double)rows.Count,
                    rows.Count == 0 ? 0 : matches / (double)rows.Count);
                if (best is null
                    || choice.Accepted > best.Accepted
                    || (choice.Accepted == best.Accepted && choice.MinMargin > best.MinMargin)
                    || (choice.Accepted == best.Accepted && Math.Abs(choice.MinMargin - best.MinMargin) < 0.000001 && choice.MinScore > best.MinScore))
                {
                    best = choice;
                }
            }
        }

        return best ?? new FastOcrThresholdChoice(
            FastOcrTemplateIndex.DefaultMinScore,
            FastOcrTemplateIndex.DefaultMinMargin,
            0,
            rows.Count(row => IsCandidateAccepted(row, FastOcrTemplateIndex.DefaultMinScore, FastOcrTemplateIndex.DefaultMinMargin) && !LabelMatches(row)),
            0,
            0);
    }

    private static bool IsCandidateAccepted(FastOcrEvaluationRow row, double minScore, double minMargin)
    {
        return !string.IsNullOrWhiteSpace(row.FastLabel)
            && row.Score >= minScore
            && row.Margin >= minMargin;
    }

    private static bool LabelMatches(FastOcrEvaluationRow row)
    {
        return !string.IsNullOrWhiteSpace(row.CleanLabel)
            && string.Equals(row.FastLabel, row.CleanLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCalibrationReport(string outputFile, IReadOnlyList<FastOcrCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteProfileCalibrationReport(string outputFile, IReadOnlyList<FastOcrProfileCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("visual_profile_id,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.VisualProfileId),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteFamilyCalibrationReport(string outputFile, IReadOnlyList<FastOcrFamilyCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("profile_family_id,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.ProfileFamilyId),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }
}
