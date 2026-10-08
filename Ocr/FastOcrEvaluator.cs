using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public static partial class FastOcrEvaluator
{
    private const double DefaultAssistMinAcceptRate = 0.60;
    private const double NameAssistMinAcceptRate = 0.95;

    private static readonly double[] ScoreCandidates = [0.99, 0.98, 0.97, 0.96, 0.95, 0.94, 0.93, 0.92, 0.91, 0.90, 0.88, 0.86, 0.84, 0.82];
    private static readonly double[] MarginCandidates = [0.20, 0.15, 0.10, 0.08, 0.06, 0.04, 0.02, 0.01, 0.00];

    public static int RunEval(string indexFile, string shadowPath)
    {
        if (!File.Exists(indexFile))
        {
            Console.Error.WriteLine($"Fast OCR index not found: {indexFile}");
            return 2;
        }

        var rows = OcrShadowDataset.ReadRows(shadowPath);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"No OCR shadow rows found: {shadowPath}");
            return 2;
        }

        var index = FastOcrTemplateIndex.Load(indexFile);
        var evaluations = EvaluateRows("eval", rows, index);
        var outputFile = ResolveOutputFile(shadowPath);
        WriteReport(outputFile, evaluations);
        var confusionFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_confusion.csv");
        WriteConfusionReport(confusionFile, evaluations);
        WriteSummary(evaluations);
        Console.WriteLine($"fast_eval.file={outputFile}");
        Console.WriteLine($"fast_eval.confusion_file={confusionFile}");
        return 0;
    }

    public static int RunCalibrate(string shadowParent, string outputFile, string? featureName = null)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length == 0)
        {
            Console.Error.WriteLine($"No non-empty ocr_shadow.csv files found: {shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var feature = ResolveFeatureName(featureName);
        var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
        var hasCrossValidation = rowsByCsv.Length >= 2;
        var evaluations = hasCrossValidation
            ? BuildCrossValidationEvaluations(rowsByCsv, feature)
            : EvaluateRows("calibrate_self", allRows, index);
        var calibration = CalibratePolicies(index, evaluations, hasCrossValidation);

        var fullOutputFile = Path.GetFullPath(outputFile);
        index.Save(fullOutputFile);

        var outputDirectory = Path.GetDirectoryName(fullOutputFile) ?? ".";
        var evalFile = Path.Combine(outputDirectory, "ocr_fast_eval.csv");
        var confusionFile = Path.Combine(outputDirectory, "ocr_fast_confusion.csv");
        var calibrationFile = Path.Combine(outputDirectory, "ocr_fast_calibration.csv");
        WriteReport(evalFile, evaluations);
        WriteConfusionReport(confusionFile, evaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteSummary(evaluations);
        WriteCalibrationSummary(calibration);
        Console.WriteLine($"fast_calibrate.index_file={fullOutputFile}");
        Console.WriteLine($"fast_calibrate.eval_file={evalFile}");
        Console.WriteLine($"fast_calibrate.confusion_file={confusionFile}");
        Console.WriteLine($"fast_calibrate.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_calibrate.shadow_runs={rowsByCsv.Length}");
        Console.WriteLine($"fast_calibrate.feature={feature}");
        Console.WriteLine($"fast_calibrate.cross_validation={hasCrossValidation.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        if (!hasCrossValidation)
        {
            Console.Error.WriteLine("Calibration kept assist disabled because at least two shadow runs are required for cross-run validation.");
        }

        return 0;
    }

    public static int RunCalibrateVisualProfiles(string shadowParent, string outputFile, string? featureName = null)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length == 0)
        {
            Console.Error.WriteLine($"No non-empty ocr_shadow.csv files found: {shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var feature = ResolveFeatureName(featureName);
        var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
        var profileCount = allRows
            .Select(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasCrossValidation = rowsByCsv.Length >= 2;
        var evaluations = hasCrossValidation
            ? BuildCrossValidationEvaluations(rowsByCsv, feature, ProfileRoutingMode.Family)
            : EvaluateRows("calibrate_self", allRows, index);
        var crossProfileEvaluations = profileCount >= 2
            ? BuildCrossProfileEvaluations(rowsByCsv, feature, ProfileRoutingMode.Family)
            : Array.Empty<FastOcrEvaluationRow>();
        var familyCalibrationEvaluations = evaluations
            .Concat(crossProfileEvaluations.Where(ShouldUseCrossProfileEvaluationForFamilyCalibration))
            .ToArray();
        var calibration = CalibratePolicies(index, evaluations, hasCrossValidation);
        var profileCalibration = CalibrateProfilePolicies(index, evaluations, hasCrossValidation);
        var familyCalibration = CalibrateFamilyPolicies(index, familyCalibrationEvaluations, hasCrossValidation);

        var fullOutputFile = Path.GetFullPath(outputFile);
        index.Save(fullOutputFile);

        var outputDirectory = Path.GetDirectoryName(fullOutputFile) ?? ".";
        var evalFile = Path.Combine(outputDirectory, "ocr_fast_eval.csv");
        var crossProfileEvalFile = Path.Combine(outputDirectory, "ocr_fast_cross_profile_eval.csv");
        var confusionFile = Path.Combine(outputDirectory, "ocr_fast_confusion.csv");
        var calibrationFile = Path.Combine(outputDirectory, "ocr_fast_calibration.csv");
        var profileCalibrationFile = Path.Combine(outputDirectory, "ocr_fast_profile_calibration.csv");
        var familyCalibrationFile = Path.Combine(outputDirectory, "ocr_fast_family_calibration.csv");
        WriteReport(evalFile, evaluations);
        if (crossProfileEvaluations.Count > 0)
        {
            WriteReport(crossProfileEvalFile, crossProfileEvaluations);
        }

        WriteConfusionReport(confusionFile, evaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteProfileCalibrationReport(profileCalibrationFile, profileCalibration);
        WriteFamilyCalibrationReport(familyCalibrationFile, familyCalibration);
        WriteSummary(evaluations);
        WriteCalibrationSummary(calibration);
        foreach (var row in profileCalibration)
        {
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }
        foreach (var row in familyCalibration)
        {
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }

        Console.WriteLine($"fast_calibrate_visual.index_file={fullOutputFile}");
        Console.WriteLine($"fast_calibrate_visual.eval_file={evalFile}");
        Console.WriteLine($"fast_calibrate_visual.cross_profile_eval_file={(crossProfileEvaluations.Count > 0 ? crossProfileEvalFile : "")}");
        Console.WriteLine($"fast_calibrate_visual.confusion_file={confusionFile}");
        Console.WriteLine($"fast_calibrate_visual.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.profile_calibration_file={profileCalibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.family_calibration_file={familyCalibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.shadow_runs={rowsByCsv.Length}");
        Console.WriteLine($"fast_calibrate_visual.visual_profiles={profileCount}");
        Console.WriteLine($"fast_calibrate_visual.feature={feature}");
        Console.WriteLine($"fast_calibrate_visual.cross_validation={hasCrossValidation.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        return 0;
    }

    private static bool ShouldUseCrossProfileEvaluationForFamilyCalibration(FastOcrEvaluationRow row)
    {
        return row.Reason.Contains("profile_family:", StringComparison.OrdinalIgnoreCase);
    }

    public static int RunCrossValidate(string shadowParent)
    {
        var csvFiles = OcrShadowDataset.FindCsvFiles(shadowParent);
        if (csvFiles.Count < 2)
        {
            Console.Error.WriteLine($"Cross validation needs at least two ocr_shadow.csv files. Found={csvFiles.Count}, path={shadowParent}");
            return 2;
        }

        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length < 2)
        {
            Console.Error.WriteLine($"Cross validation needs at least two non-empty ocr_shadow.csv files. Found={rowsByCsv.Length}, path={shadowParent}");
            return 2;
        }

        var allEvaluations = BuildCrossValidationEvaluations(rowsByCsv);

        var outputFile = Path.Combine(Path.GetFullPath(shadowParent), "ocr_fast_eval.csv");
        WriteReport(outputFile, allEvaluations);
        var confusionFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_confusion.csv");
        var calibrationFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_calibration.csv");
        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var calibrationIndex = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine);
        var calibration = CalibratePolicies(calibrationIndex, allEvaluations, hasCrossValidation: true);
        WriteConfusionReport(confusionFile, allEvaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteSummary(allEvaluations);
        WriteCalibrationSummary(calibration);
        Console.WriteLine($"fast_eval.file={outputFile}");
        Console.WriteLine($"fast_eval.confusion_file={confusionFile}");
        Console.WriteLine($"fast_eval.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_eval.folds={rowsByCsv.Length}");
        return 0;
    }

    public static int RunFeatureEval(string shadowParent)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length < 2)
        {
            Console.Error.WriteLine($"Feature evaluation needs at least two non-empty ocr_shadow.csv files. Found={rowsByCsv.Length}, path={shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var features = new[]
        {
            FastOcrTemplateIndex.CurrentFeature,
            FastOcrTemplateIndex.ExperimentalFeature,
            FastOcrTemplateIndex.CanonicalFeature
        };
        var reportRows = new List<FastOcrFeatureEvalRow>();
        foreach (var feature in features)
        {
            var evaluations = BuildCrossValidationEvaluations(rowsByCsv, feature);
            var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
            var calibration = CalibratePolicies(index, evaluations, hasCrossValidation: true);
            foreach (var row in calibration
                .Where(row => row.FieldKey.Equals("name", StringComparison.OrdinalIgnoreCase)
                    || row.FieldKey.Equals("subStat4", StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase))
            {
                var fieldRows = evaluations
                    .Where(value => value.FieldKey.Equals(row.FieldKey, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                reportRows.Add(new FastOcrFeatureEvalRow(
                    feature,
                    row.FieldKey,
                    row.Rows,
                    row.Accepted,
                    row.FalseAccepts,
                    row.AcceptRate,
                    row.MatchRate,
                    row.MinScore,
                    row.MinMargin,
                    row.AssistEnabled,
                    row.Reason,
                    fieldRows.Length == 0 ? 0 : fieldRows.Average(value => value.Score),
                    fieldRows.Length == 0 ? 0 : fieldRows.Average(value => value.Margin)));
            }
        }

        var outputFile = Path.Combine(Path.GetFullPath(shadowParent), "ocr_fast_feature_eval.csv");
        WriteFeatureEvalReport(outputFile, reportRows);
        foreach (var row in reportRows)
        {
            var featureKey = SanitizeKey(row.Feature);
            var fieldKey = SanitizeKey(row.FieldKey);
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        }

        Console.WriteLine($"feature_eval.file={outputFile}");
        Console.WriteLine($"feature_eval.folds={rowsByCsv.Length}");
        return 0;
    }

    private static (string file, IReadOnlyList<OcrShadowDatasetRow> rows)[] ReadNonEmptyShadowRuns(string shadowParent)
    {
        return OcrShadowDataset.FindCsvFiles(shadowParent)
            .Select(file => (file, rows: OcrShadowDataset.ReadRowsFromCsv(file)))
            .Where(item => item.rows.Count > 0)
            .ToArray();
    }

    private static string ResolveFeatureName(string? featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName)
            || string.Equals(featureName, "v3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.CurrentFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.CurrentFeature;
        }

        if (string.Equals(featureName, "v4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.ExperimentalFeature;
        }

        if (string.Equals(featureName, "v6", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, "canonical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.CanonicalFeature;
        }

        throw new ArgumentException($"Unsupported fast OCR feature: {featureName}");
    }

    private static IReadOnlyList<FastOcrEvaluationRow> BuildCrossValidationEvaluations(
        IReadOnlyList<(string file, IReadOnlyList<OcrShadowDatasetRow> rows)> rowsByCsv,
        string featureName = FastOcrTemplateIndex.CurrentFeature,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var allEvaluations = new List<FastOcrEvaluationRow>();
        foreach (var test in rowsByCsv)
        {
            var trainRows = rowsByCsv
                .Where(item => !string.Equals(item.file, test.file, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.rows)
                .ToArray();
            var index = FastOcrTemplateIndex.Build(trainRows, Console.Error.WriteLine, featureName);
            var fold = Path.GetFileName(Path.GetDirectoryName(test.file) ?? test.file);
            allEvaluations.AddRange(EvaluateRows(fold, test.rows, index, routingMode));
        }

        return allEvaluations;
    }

    private static IReadOnlyList<FastOcrEvaluationRow> BuildCrossProfileEvaluations(
        IReadOnlyList<(string file, IReadOnlyList<OcrShadowDatasetRow> rows)> rowsByCsv,
        string featureName = FastOcrTemplateIndex.CurrentFeature,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var profiles = allRows
            .Select(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allEvaluations = new List<FastOcrEvaluationRow>();
        foreach (var profile in profiles)
        {
            var trainRows = allRows
                .Where(row => !FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId).Equals(profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var testRows = allRows
                .Where(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId).Equals(profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var index = FastOcrTemplateIndex.Build(trainRows, Console.Error.WriteLine, featureName);
            allEvaluations.AddRange(EvaluateRows($"profile_{profile}", testRows, index, routingMode));
        }

        return allEvaluations;
    }

    private static IReadOnlyList<FastOcrEvaluationRow> EvaluateRows(
        string fold,
        IReadOnlyList<OcrShadowDatasetRow> rows,
        FastOcrTemplateIndex index,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var evaluations = new List<FastOcrEvaluationRow>();
        foreach (var row in rows)
        {
            if (!FastOcrTemplateIndex.IsSupportedField(row.FieldKey))
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            FastOcrMatch match;
            if (string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))
            {
                match = FastOcrMatch.Empty(row.FieldKey, "missing_image");
            }
            else
            {
                using var bitmap = new Bitmap(row.ResolvedImageFile);
                match = index.Match(row.FieldKey, bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height), row.VisualProfileId, routingMode);
            }
            sw.Stop();

            var accepted = index.IsMatchAccepted(match, requireAssistEnabled: false);
            var matchesClean = accepted && string.Equals(match.Label, row.CleanLabel, StringComparison.OrdinalIgnoreCase);
            evaluations.Add(new FastOcrEvaluationRow(
                fold,
                row.CsvFile,
                row.ItemIndex,
                row.RoiIndex,
                FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId),
                FastOcrTemplateIndex.ProfileFamilyId(row.VisualProfileId),
                row.FieldKey,
                row.CleanLabel,
                match.Label,
                match.Score,
                match.Top2Label,
                match.Top2Score,
                match.Margin,
                match.AssistEnabled,
                accepted,
                matchesClean,
                accepted && !matchesClean,
                sw.Elapsed.TotalMilliseconds,
                match.SourceFamilyId,
                match.CanonicalCropSucceeded,
                match.CanonicalCropFallback,
                match.FeatureElapsedMs,
                match.Reason));
        }

        return evaluations;
    }
}
