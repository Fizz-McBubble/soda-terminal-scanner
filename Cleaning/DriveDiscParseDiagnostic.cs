using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Cleaning;

/// <summary>
/// A privacy-safe projection of the Cleaner errors that are already structured.
/// Raw OCR text is intentionally not retained here.
/// </summary>
public sealed partial record DriveDiscParseDiagnostic(
    string Code,
    string FieldKind,
    int? Slot,
    IReadOnlyList<string> Candidates,
    int? ObservedCount,
    int? RequiredCount)
{
    public IReadOnlyList<DriveDiscParseOption> Options { get; init; } = Array.Empty<DriveDiscParseOption>();

    public IReadOnlyList<int> MissingPositions { get; init; } = Array.Empty<int>();

    public static DriveDiscParseDiagnostic? From(Exception exception)
    {
        const string ambiguousPrefix = "stat_domain_ambiguous:";
        const string candidatesDelimiter = ":candidates=";
        if (exception.Message.StartsWith(ambiguousPrefix, StringComparison.Ordinal))
        {
            var candidatesIndex = exception.Message.IndexOf(candidatesDelimiter, StringComparison.Ordinal);
            var header = candidatesIndex >= 0
                ? exception.Message[ambiguousPrefix.Length..candidatesIndex]
                : string.Empty;
            var parts = header.Split(':', StringSplitOptions.None);
            if (parts.Length >= 2
                && (parts[0] is "main" or "sub")
                && int.TryParse(parts[1], out var slot)
                && slot is >= 1 and <= 6
                && candidatesIndex >= 0)
            {
                return new DriveDiscParseDiagnostic(
                    "stat_domain_ambiguous",
                    parts[0],
                    slot,
                    exception.Message[(candidatesIndex + candidatesDelimiter.Length)..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    null,
                    null);
            }
        }

        const string incompletePrefix = "substat_domain_incomplete:";
        if (exception.Message.StartsWith(incompletePrefix, StringComparison.Ordinal))
        {
            var counts = exception.Message[incompletePrefix.Length..].Split('/', StringSplitOptions.None);
            if (counts.Length == 2
                && int.TryParse(counts[0], out var observed)
                && int.TryParse(counts[1], out var required))
            {
                return new DriveDiscParseDiagnostic(
                    "substat_domain_incomplete",
                    "sub",
                    null,
                    Array.Empty<string>(),
                    observed,
                    required);
            }
        }

        return null;
    }
}

public sealed record DriveDiscParseOption(string Stat, object Value, int? Position);

public sealed class DriveDiscPartialParseException : Exception
{
    public DriveDiscPartialParseException(
        DriveDiscExport partialExport,
        DriveDiscParseDiagnostic diagnostic,
        Exception? innerException = null)
        : base(diagnostic.Code, innerException)
    {
        PartialExport = partialExport;
        Diagnostic = diagnostic;
    }

    public DriveDiscExport PartialExport { get; }

    public DriveDiscParseDiagnostic Diagnostic { get; }
}
