using System.Text.Json;

namespace ZZZScannerHelper;

internal static class DirectForkTaskOutput
{
    internal static string DirectoryFor(string outputRoot) => Path.Combine(outputRoot, "run");

    internal static string? FileFor(string outputRoot, string name)
    {
        var directory = DirectoryFor(outputRoot);
        if (!Directory.Exists(directory)) return null;
        if ((File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("direct_fork_output_reparse");
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("direct_fork_output_reparse");
        return path;
    }

    internal static bool MatchesTerminal(string outputRoot, JsonElement terminal)
    {
        if (!terminal.TryGetProperty("OutputDirectory", out var directory)
            || directory.ValueKind != JsonValueKind.String) return false;
        var value = directory.GetString();
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            && Path.GetFullPath(value).Equals(Path.GetFullPath(DirectoryFor(outputRoot)), StringComparison.OrdinalIgnoreCase);
    }
}
