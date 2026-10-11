using System.Text;

namespace ZZZScannerNext.Cleaning;

// Names have different safety requirements from numeric/stat OCR. Rank aliases
// by canonical set, keep ties unresolved, and never use unverified extra names
// as automatic corrections. Raw OCR remains in the R4 evidence.
public sealed class DiscNameMatcher
{
    private readonly (string Name, string[] Aliases)[] _sets;

    public DiscNameMatcher(IEnumerable<DiscSet> sets)
    {
        _sets = sets.GroupBy(set => set.Name, StringComparer.Ordinal)
            .Select(group => (group.Key, group.SelectMany(set => set.Aliases.Append(set.Name))
                .Select(Normalize).Where(alias => alias.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    public DiscNameMatch Match(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return new(text, false, 0, "unresolved_catalog_name");
        var exact = _sets.Where(set => set.Aliases.Contains(normalized, StringComparer.Ordinal)).ToArray();
        if (exact.Length == 1) return new(exact[0].Name, true, 1, "exact_versioned_catalog_alias");
        if (exact.Length > 1) return new(text, false, 1, "ambiguous_catalog_name");

        var ranked = _sets.Select(set =>
        {
            // Short aliases such as 雪兔/嘉音 are exact-only. One character of
            // a two/three-character nickname is not enough to infer a set.
            var aliases = set.Aliases.Where(alias => alias.Length >= 4).ToArray();
            var score = aliases.Length == 0 ? 0 : aliases.Max(alias => StringMatcher.Score(alias, normalized));
            var singleEdit = normalized.Length >= 3 && aliases.Any(alias =>
                StringMatcher.Distance(alias, normalized) == 1 && StringMatcher.Score(alias, normalized) >= 0.75f);
            return (set.Name, Score: score, SingleEdit: singleEdit);
        }).OrderByDescending(set => set.Score).ToArray();
        if (ranked.Length == 0) return new(text, false, 0, "unresolved_catalog_name");
        var best = ranked[0];
        var runnerUp = ranked.Length > 1 ? ranked[1].Score : 0;
        if (best.SingleEdit && best.Score - runnerUp >= 0.15f - 0.00001f)
            return new(best.Name, true, best.Score, "unique_single_edit_catalog_alias");
        return new(text, false, best.Score, best.SingleEdit ? "ambiguous_catalog_name" : "unresolved_catalog_name");
    }

    public static string Normalize(string text)
    {
        var widthNormalized = (text ?? "").Normalize(NormalizationForm.FormKC);
        var simplified = StringMatcher.SimplifyChinese(widthNormalized);
        // Legacy conversion may use an unavailable system code page. Never let
        // replacement characters shorten a name into an unrelated alias.
        if (simplified.Count(character => character == '?') > widthNormalized.Count(character => character == '?'))
            simplified = widthNormalized;
        return string.Concat(simplified.Where(char.IsLetter)).ToLowerInvariant();
    }
}

public readonly record struct DiscNameMatch(string Name, bool Accepted, float Score, string Rule);
