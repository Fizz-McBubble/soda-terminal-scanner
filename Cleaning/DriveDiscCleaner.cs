using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using System.Text.RegularExpressions;

namespace ZZZScannerNext.Cleaning;

public sealed class DriveDiscCleaner
{
    private readonly WikiData _wikiData;
    private readonly List<string> _levelCandidates = new();

    public DriveDiscCleaner(WikiData wikiData)
    {
        _wikiData = wikiData;
        for (var max = 9; max <= 15; max += 3)
        {
            for (var level = 0; level <= max; level++)
            {
                _levelCandidates.Add($"{level:D2}/{max:D2}");
            }
        }
    }

    public DriveDiscExport Clean(int index, string rarity, IReadOnlyList<OcrResult> result)
    {
        if (result.Count < 4)
        {
            throw new InvalidDataException($"OCR结果不足：{result.Count}/4。");
        }

        var export = new DriveDiscExport
        {
            Index = index,
            Rarity = rarity,
            RawOcr = string.Join(" | ", result.Select(r => r.Text))
        };
        DriveDiscParseDiagnostic? partialDiagnostic = null;

        for (var i = 0; i < result.Count; i++)
        {
            var text = result[i].Text;
            switch (i)
            {
                case 0:
                    (export.Name, export.Slot) = CleanName(text);
                    break;
                case 1:
                    (export.Level, export.MaxLevel) = CleanLevel(text);
                    break;
                case 2 when i + 1 < result.Count:
                    {
                        if (string.IsNullOrWhiteSpace(result[i + 1].Text))
                        {
                            throw new InvalidDataException($"主属性OCR结果为空：{export.RawOcr}");
                        }

                        try
                        {
                            var key = CleanStatByDomain(
                                text,
                                result[i + 1].Text,
                                rarity,
                                mainStat: true,
                                export.Slot,
                                export.Level,
                                allowOneCharacterNumericRepair: HasStatLabel(text));
                            var value = CleanStatValue(result[i + 1].Text, key, rarity, true, export.Slot, export.Level);
                            export.MainStat[key] = value;
                        }
                        catch (InvalidDataException exception)
                        {
                            var diagnostic = DriveDiscParseDiagnostic.From(exception);
                            if (diagnostic is null) throw;
                            partialDiagnostic = EnrichDiagnostic(
                                diagnostic,
                                result[i + 1].Text,
                                rarity,
                                mainStat: true,
                                export.Slot,
                                export.Level,
                                position: null);
                        }
                        i++;
                        break;
                    }
                case 4:
                case 6:
                case 8:
                case 10:
                    if (i + 1 >= result.Count)
                    {
                        break;
                    }

                    var subStatText = text;
                    var subValueText = result[i + 1].Text;
                    if (string.IsNullOrWhiteSpace(subValueText)
                        && TrySplitMergedSubStat(text, rarity, export.Slot, export.Level, out var mergedStat, out var mergedValue))
                    {
                        subStatText = mergedStat;
                        subValueText = mergedValue;
                    }

                    if (string.IsNullOrWhiteSpace(subStatText) || string.IsNullOrWhiteSpace(subValueText))
                    {
                        break;
                    }

                    if (IsSetEffectText(subStatText) || IsSetEffectText(subValueText))
                    {
                        return ValidateSlotSafety(export);
                    }

                    try
                    {
                        var subKey = CleanStatByDomain(
                            subStatText,
                            subValueText,
                            rarity,
                            mainStat: false,
                            export.Slot,
                            export.Level,
                            allowOneCharacterNumericRepair: true);
                        var subValue = CleanStatValue(subValueText, subKey, rarity, false, export.Slot);
                        export.SubStats.Add(new Dictionary<string, object> { [subKey] = subValue });
                    }
                    catch (InvalidDataException exception)
                    {
                        var diagnostic = DriveDiscParseDiagnostic.From(exception);
                        if (diagnostic is null) throw;
                        partialDiagnostic ??= EnrichDiagnostic(
                            diagnostic,
                            subValueText,
                            rarity,
                            mainStat: false,
                            export.Slot,
                            export.Level,
                            export.SubStats.Count);
                    }
                    i++;
                    break;
            }
        }

        var requiredSubStats = export.Level >= 3 ? 4 : 3;
        if (result.Count > 4 && export.SubStats.Count < requiredSubStats)
        {
            partialDiagnostic ??= new DriveDiscParseDiagnostic(
                "substat_domain_incomplete",
                "sub",
                export.Slot,
                Array.Empty<string>(),
                export.SubStats.Count,
                requiredSubStats)
            {
                MissingPositions = Enumerable.Range(export.SubStats.Count, requiredSubStats - export.SubStats.Count).ToArray()
            };
        }

        if (partialDiagnostic is not null)
            throw new DriveDiscPartialParseException(export, partialDiagnostic);

        return ValidateSlotSafety(export);
    }

    private DriveDiscParseDiagnostic EnrichDiagnostic(
        DriveDiscParseDiagnostic diagnostic,
        string rawValue,
        string rarity,
        bool mainStat,
        int slot,
        int level,
        int? position)
    {
        var options = diagnostic.Candidates
            .Select(candidate =>
            {
                try
                {
                    var resolved = CleanStatValueResult(
                        rawValue,
                        candidate,
                        rarity,
                        mainStat,
                        slot,
                        mainStat ? level : null);
                    return new DriveDiscParseOption(
                        resolved.Key,
                        resolved.Value,
                        position);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            })
            .Where(option => option is not null)
            .Cast<DriveDiscParseOption>()
            .ToArray();
        return diagnostic with
        {
            Options = options,
            MissingPositions = position is int index ? [index] : Array.Empty<int>()
        };
    }

    private DriveDiscExport ValidateSlotSafety(DriveDiscExport export)
    {
        var issues = DriveDiscSlotSafety.ValidateAndRepair(export, _wikiData.StatRules);
        if (issues.Count > 0)
        {
            throw new InvalidDataException($"槽位安全校验失败：{DriveDiscSlotSafety.FormatIssues(issues)} OCR={export.RawOcr}");
        }

        return export;
    }

    private (string Name, int Slot) CleanName(string name)
    {
        var slot = name.FirstOrDefault(c => char.IsDigit(c) && "123456".Contains(c));
        var simplified = StringMatcher.SimplifyChinese(name, keepChineseAndDigits: true);
        var textOnly = string.Concat(simplified.Where(c => !char.IsDigit(c)));
        var match = StringMatcher.BestMatch(_wikiData.NameCandidates(), textOnly);
        return (_wikiData.ResolveDiscName(match.Text), slot == default ? 0 : slot - '0');
    }

    private (int Level, int MaxLevel) CleanLevel(string level)
    {
        var raw = level;
        level = StringMatcher.NumericToken(level);
        if (TryParseLevel(level, out var parsed))
        {
            return parsed;
        }

        if (level.Length == 6 && level[3] == '/' && TryParseLevel(level[1..], out parsed))
        {
            return parsed;
        }

        level = StringMatcher.BestMatch(_levelCandidates, level, 0.3f).Text;
        if (TryParseLevel(level, out parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"等级识别失败：{raw}");
    }

    private static bool TryParseLevel(string level, out (int Level, int MaxLevel) result)
    {
        result = default;
        if (level.Length != 5 || level[2] != '/')
        {
            return false;
        }

        var parts = level.Split('/', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var current) || !int.TryParse(parts[1], out var max))
        {
            return false;
        }

        result = (current, max);
        return true;
    }

    private string CleanMainStat(string stat, int slot)
    {
        stat = NormalizeStatName(stat, _wikiData.StatRules.MainStatAliases);
        var candidates = _wikiData.StatRules.SlotMainStats.TryGetValue(slot.ToString(), out var slotStats)
            ? slotStats
            : _wikiData.StatRules.SlotMainStats.Values.SelectMany(x => x).Distinct().ToList();

        var match = StringMatcher.BestMatch(candidates, stat);
        return match.Text;
    }

    private string CleanSubStat(string stat)
    {
        stat = NormalizeStatName(stat, _wikiData.StatRules.SubStatAliases);
        return StringMatcher.BestMatch(_wikiData.StatRules.SubStats, stat).Text;
    }

    private string CleanStatByDomain(
        string rawStat,
        string rawValue,
        string rarity,
        bool mainStat,
        int slot,
        int level,
        bool allowOneCharacterNumericRepair = false)
    {
        var aliases = mainStat ? _wikiData.StatRules.MainStatAliases : _wikiData.StatRules.SubStatAliases;
        var normalized = NormalizeStatName(rawStat, aliases);
        normalized = string.Concat(normalized.Where(character => !char.IsDigit(character)));
        var candidates = mainStat
            ? _wikiData.StatRules.SlotMainStats.TryGetValue(slot.ToString(), out var slotStats)
                ? slotStats
                : _wikiData.StatRules.SlotMainStats.Values.SelectMany(values => values).Distinct().ToList()
            : _wikiData.StatRules.SubStats;
        if (mainStat && slot is >= 1 and <= 3 && candidates.Count == 1)
        {
            // Upstream's fixed-slot contract is stronger than a damaged OCR label:
            // slots I-III have exactly one legal main stat.
            return candidates[0];
        }
        var source = mainStat
            ? _wikiData.StatRules.MainStatValues[rarity]
            : _wikiData.StatRules.SubStatValues[rarity];
        var token = StringMatcher.NumericToken(rawValue);
        (string Stat, float LabelScore, int ValueEvidence)[] DomainCandidates(bool allowMissingPercent) => candidates
            .Select(candidate => (
                Stat: candidate,
                LabelScore: StringMatcher.Score(candidate, normalized),
                ValueEvidence: CandidateValueKeys(source, candidate, token, mainStat, slot)
                    .Where(source.ContainsKey)
                    .Select(key => IsLegalValue(source[key], token, mainStat ? level : null, allowMissingPercent)
                        ? 2
                        : allowOneCharacterNumericRepair
                          && StringMatcher.Score(candidate, normalized) >= 0.45f
                          && IsOneCharacterNumericRepair(source[key], token, mainStat ? level : null, allowMissingPercent)
                            ? 1
                            : 0)
                    .DefaultIfEmpty(0)
                    .Max()))
            .Where(candidate => candidate.ValueEvidence > 0)
            .ToArray();

        var matches = DomainCandidates(allowMissingPercent: false);
        if (matches.Length == 0 && !token.Contains('%'))
        {
            matches = DomainCandidates(allowMissingPercent: true);
        }

        if (matches.Length > 0)
        {
            var strongestEvidence = matches.Max(candidate => candidate.ValueEvidence);
            matches = matches.Where(candidate => candidate.ValueEvidence == strongestEvidence).ToArray();
        }

        if (matches.Length == 1)
        {
            return matches[0].Stat;
        }

        var textual = matches.Where(candidate =>
                !string.IsNullOrWhiteSpace(normalized)
                && (candidate.Stat.Contains(normalized, StringComparison.Ordinal)
                    || normalized.Contains(candidate.Stat, StringComparison.Ordinal)))
            .ToArray();
        if (textual.Length == 1)
        {
            return textual[0].Stat;
        }

        var ranked = matches.OrderByDescending(candidate => candidate.LabelScore).ToArray();
        if (ranked.Length > 0
            && ranked[0].LabelScore >= 0.45f
            && (ranked.Length == 1 || ranked[0].LabelScore - ranked[1].LabelScore >= 0.20f))
        {
            return ranked[0].Stat;
        }

        throw new InvalidDataException(
            $"stat_domain_ambiguous:{(mainStat ? "main" : "sub")}:{slot}:{rawStat}:{rawValue}:candidates={string.Join(',', matches.Select(candidate => candidate.Stat))}");
    }

    private bool TrySplitMergedSubStat(
        string raw,
        string rarity,
        int slot,
        int level,
        out string stat,
        out string value)
    {
        stat = string.Empty;
        value = string.Empty;
        var upgradeAndValue = Regex.Match(raw, @"\+\s*[0-5](?<value>\d+(?:\.\d+)?%?)");
        if (upgradeAndValue.Success)
        {
            stat = raw[..upgradeAndValue.Index];
            value = upgradeAndValue.Groups["value"].Value;
        }
        else
        {
            var firstDigit = raw.IndexOfAny("0123456789".ToCharArray());
            if (firstDigit <= 0) return false;
            stat = raw[..firstDigit];
            value = StringMatcher.NumericToken(raw[firstDigit..]);
        }

        if (string.IsNullOrWhiteSpace(stat) || string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            _ = CleanStatByDomain(stat, value, rarity, mainStat: false, slot, level, allowOneCharacterNumericRepair: true);
            return true;
        }
        catch (InvalidDataException)
        {
            stat = string.Empty;
            value = string.Empty;
            return false;
        }
    }

    private static bool IsLegalValue(
        StatValueRange range,
        string token,
        int? level,
        bool allowMissingPercent)
    {
        if (token.Contains('%') != range.IsPercent
            && !(allowMissingPercent && range.IsPercent && !token.Contains('%')))
        {
            return false;
        }

        var normalized = token.Trim().TrimEnd('%');
        if (!float.TryParse(normalized, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var observed))
        {
            return false;
        }

        var values = range.All;
        var candidates = level is >= 0 && level < values.Length
            ? [values[level.Value]]
            : values;
        return candidates.Any(value =>
            float.TryParse(value.TrimEnd('%'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var expected)
            && Math.Abs(observed - expected) <= 0.051f);
    }

    private static bool IsOneCharacterNumericRepair(
        StatValueRange range,
        string token,
        int? level,
        bool allowMissingPercent)
    {
        if (token.Contains('%') != range.IsPercent
            && !(allowMissingPercent && range.IsPercent && !token.Contains('%')))
        {
            return false;
        }

        var observed = token.Trim();
        if (range.IsPercent && !observed.Contains('%')) observed += "%";
        var values = range.All;
        var candidates = level is >= 0 && level < values.Length
            ? [values[level.Value]]
            : values;
        return candidates.Any(value =>
            EditDistance(observed, value) == 1
            || IsCompressedDecimalEquivalent(observed, value));
    }

    private static bool IsCompressedDecimalEquivalent(string observed, string expected)
    {
        static string DigitsWithoutTrailingFractionZeros(string value)
        {
            var numeric = value.Trim().TrimEnd('%');
            var pieces = numeric.Split('.', 2);
            if (pieces.Length == 1) return pieces[0];
            var fraction = pieces[1].TrimEnd('0');
            return fraction.Length == 0 ? pieces[0] : pieces[0] + fraction;
        }

        return DigitsWithoutTrailingFractionZeros(observed)
            == DigitsWithoutTrailingFractionZeros(expected);
    }

    private static int EditDistance(string source, string target)
    {
        var previous = Enumerable.Range(0, target.Length + 1).ToArray();
        for (var sourceIndex = 1; sourceIndex <= source.Length; sourceIndex++)
        {
            var current = new int[target.Length + 1];
            current[0] = sourceIndex;
            for (var targetIndex = 1; targetIndex <= target.Length; targetIndex++)
            {
                var cost = source[sourceIndex - 1] == target[targetIndex - 1] ? 0 : 1;
                current[targetIndex] = Math.Min(
                    Math.Min(current[targetIndex - 1] + 1, previous[targetIndex] + 1),
                    previous[targetIndex - 1] + cost);
            }

            previous = current;
        }

        return previous[target.Length];
    }

    private object CleanStatValue(string value, string stat, string rarity, bool mainStat, int slot, int? level = null)
        => CleanStatValueResult(value, stat, rarity, mainStat, slot, level).Value;

    private (string Key, object Value) CleanStatValueResult(
        string value,
        string stat,
        string rarity,
        bool mainStat,
        int slot,
        int? level = null)
    {
        var source = mainStat
            ? _wikiData.StatRules.MainStatValues[rarity]
            : _wikiData.StatRules.SubStatValues[rarity];

        var token = StringMatcher.NumericToken(value);
        var keys = CandidateValueKeys(source, stat, token, mainStat, slot);
        var bestKey = "";
        var bestText = token;
        var bestScore = float.MinValue;
        StatValueRange? bestRange = null;

        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var range))
            {
                continue;
            }

            var comparable = range.IsPercent && !token.Contains('%') ? $"{token}%" : token;
            var values = mainStat && level is >= 0 && level < range.All.Length
                ? new[] { range.All[level.Value] }
                : range.All;
            if (mainStat && level is >= 0 && level < range.All.Length)
            {
                return (key, range.ToExportValue(values[0]));
            }
            var match = StringMatcher.BestMatch(values, comparable, 0.2f);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestText = match.Text;
                bestRange = range;
                bestKey = key;
            }
        }

        if (bestRange is null)
        {
            throw new InvalidDataException($"No value rule for {rarity} {stat} ({value}).");
        }

        return (bestKey, bestRange.ToExportValue(bestText));
    }

    private static string NormalizeStatName(string stat, IReadOnlyDictionary<string, string> aliases)
    {
        stat = StringMatcher.SimplifyChinese(stat, keepChineseAndDigits: true);
        stat = string.Concat(stat.Where(character => !char.IsDigit(character)));
        stat = stat.Replace("百分比", string.Empty, StringComparison.Ordinal);
        return aliases.TryGetValue(stat, out var alias) ? alias : stat;
    }

    private static bool HasStatLabel(string value) =>
        StringMatcher.SimplifyChinese(value, keepChineseAndDigits: true).Any(character => !char.IsDigit(character));

    private static bool IsSetEffectText(string text)
    {
        var simplified = StringMatcher.SimplifyChinese(text, keepChineseAndDigits: true);
        return simplified.Contains("套装", StringComparison.Ordinal)
            || simplified.Contains("效果", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> CandidateValueKeys(
        IReadOnlyDictionary<string, StatValueRange> source,
        string stat,
        string token,
        bool mainStat,
        int slot)
    {
        if (mainStat && slot is >= 1 and <= 3)
        {
            return [stat];
        }

        var keys = new List<string>();
        var percentKey = $"{stat}%";
        var forceMainPercent = mainStat && slot >= 4 && stat is "生命值" or "攻击力" or "防御力";

        if (forceMainPercent || token.Contains('%') || !source.ContainsKey(stat))
        {
            keys.Add(percentKey);
        }

        keys.Add(stat);

        if (!keys.Contains(percentKey))
        {
            keys.Add(percentKey);
        }

        return keys.Distinct().ToArray();
    }
}
