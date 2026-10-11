namespace ZZZScannerHelper;

internal static class ScannerOriginPolicy
{
    internal const string DefaultPublicOrigin = "https://soda-terminal-production.up.railway.app";
    internal const string AdditionalOriginFile = "scanner-public-origin.txt";
    internal const string WorkersPublicOrigin = "https://app.sodaterminal.workers.dev";
    internal const string DomainPublicOrigin = "https://sodaterminal.com";

    internal static string? ReadAdditionalOrigin()
    {
        var path = Path.Combine(AppContext.BaseDirectory, AdditionalOriginFile);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) :
                Environment.GetEnvironmentVariable("SODA_SCANNER_HTTPS_ORIGIN");
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool IsTrustedHttpsOrigin(string? candidate, string? configured)
    {
        // Railway remains a rollback origin. A legacy one-line package or a new
        // two-line package may explicitly approve only these reviewed Soda hosts.
        if (IsConfiguredHttpsOrigin(candidate, DefaultPublicOrigin)) return true;
        var origins = ParseAdditionalOrigins(configured);
        return origins.Any(origin => IsConfiguredHttpsOrigin(candidate, origin));
    }

    internal static string[] ParseAdditionalOrigins(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return [];
        var origins = configured.TrimEnd('\r', '\n').Split('\n').Select(value => value.TrimEnd('\r')).ToArray();
        if (origins.Length is < 1 or > 2 || origins.Distinct(StringComparer.Ordinal).Count() != origins.Length ||
            origins.Any(origin => origin != WorkersPublicOrigin && origin != DomainPublicOrigin)) return [];
        return origins;
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
    internal const int MaximumTokens = 64;
    private readonly Dictionary<string, (string Origin, DateTimeOffset Expires)> _tokens = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal string Issue(string origin, DateTimeOffset now)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        lock (_gate)
        {
            RemoveExpired(now);
            if (_tokens.Count >= MaximumTokens)
                _tokens.Remove(_tokens.MinBy(pair => pair.Value.Expires).Key);
            _tokens[token] = (origin, now.AddHours(8));
        }
        return token;
    }

    internal bool Authorized(string? token, string? origin, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrWhiteSpace(origin)) return false;
        lock (_gate)
        {
            RemoveExpired(now);
            return _tokens.TryGetValue(token, out var pairing)
                && origin.Equals(pairing.Origin, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _tokens.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
            _tokens.Remove(key);
    }

    internal bool Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate) return _tokens.Remove(token);
    }
}
