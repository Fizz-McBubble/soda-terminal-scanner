namespace ZZZScannerHelper;

internal static class ScannerOriginPolicy
{
    internal const string DefaultPublicOrigin = "https://soda-terminal-production.up.railway.app";
    internal const string AdditionalOriginFile = "scanner-public-origin.txt";

    internal static string? ReadAdditionalOrigin()
    {
        var path = Path.Combine(AppContext.BaseDirectory, AdditionalOriginFile);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() :
                Environment.GetEnvironmentVariable("SODA_SCANNER_HTTPS_ORIGIN");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool IsTrustedHttpsOrigin(string? candidate, string? configured)
    {
        // The old public site remains available for rollback. At most one additional
        // exact HTTPS origin may be supplied by a candidate package (or local dev).
        return IsConfiguredHttpsOrigin(candidate, DefaultPublicOrigin) ||
            IsConfiguredHttpsOrigin(candidate, configured);
    }

    internal static bool IsConfiguredHttpsOrigin(string? candidate, string? configured)
    {
        if (!TryOrigin(candidate, out var requested) || !TryOrigin(configured, out var approved)) return false;
        if (approved.Scheme != Uri.UriSchemeHttps || approved.IsLoopback) return false;
        return Uri.Compare(requested, approved, UriComponents.SchemeAndServer,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool TryOrigin(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length != 0 ||
            parsed.AbsolutePath != "/" || parsed.Query.Length != 0 || parsed.Fragment.Length != 0) return false;
        var normalized = parsed.GetLeftPart(UriPartial.Authority);
        if (!value.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }
}

internal sealed class ScannerPairingStore
{
    private readonly Dictionary<string, (string Origin, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal string Issue(string origin, DateTimeOffset now)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (_gate) _tokens[token] = (origin, now.AddHours(8));
        return token;
    }

    internal bool Authorized(string? token, string? origin, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate)
        {
            if (!_tokens.TryGetValue(token, out var pairing)) return false;
            if (pairing.Expires <= now) { _tokens.Remove(token); return false; }
            return origin is null || origin.Equals(pairing.Origin, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal bool Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate) return _tokens.Remove(token);
    }
}
