using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using System.Text.RegularExpressions;

namespace ZZZScannerNext.Cleaning;

public sealed partial class DriveDiscCleaner
{
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
