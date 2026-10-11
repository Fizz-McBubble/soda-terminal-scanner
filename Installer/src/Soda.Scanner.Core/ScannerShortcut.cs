using System.Text;

namespace Soda.Scanner.Core;

public sealed record ScannerShortcutChange(bool Created, string? PreviousContents = null);

// One current-user entry; content identifies its owner and exact managed root.
public static class ScannerShortcut
{
    public const string Name = "Soda Terminal · 画面扫描.url";
    public static string PathFor(string root, bool testMode) => testMode
        ? FileSystemSafety.SafePath(root, Name)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name);
    public static string Contents(string root, string origin) => "[InternetShortcut]\r\nURL=" + origin +
        "/system/scanner\r\nIconFile=" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\r\nIconIndex=0\r\n";
    public static string LegacyContents(string root, string origin) => "[InternetShortcut]\r\nURL=soda-terminal-scanner://open?origin=" +
        Uri.EscapeDataString(origin) + "\r\nIconFile=" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\r\nIconIndex=0\r\n";
    public static bool Create(string root, string origin, bool testMode) => Apply(root, origin, testMode).Created;
    public static ScannerShortcutChange Apply(string root, string origin, bool testMode)
    {
        if (!ScannerConstants.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) throw new InvalidOperationException("scanner_public_origin_invalid");
        var path = PathFor(root, testMode); FileSystemSafety.AssertNoReparsePoints(path);
        var body = Contents(root, origin);
        if (File.Exists(path))
        {
            var previous = File.ReadAllText(path);
            if (previous == body) return new(false);
            if (previous != LegacyContents(root, origin)) throw new InvalidOperationException("scanner_start_menu_conflict");
            WriteAtomic(path, body, previous);
            return new(false, previous);
        }
        WriteAtomic(path, body, null);
        return new(true);
    }
    private static void WriteAtomic(string path, string body, string? expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new StreamWriter(stream, new UnicodeEncoding(false, true)); writer.Write(body);
            }
            FileSystemSafety.AssertNoReparsePoints(path);
            if (expected is null ? File.Exists(path) : !File.Exists(path) || File.ReadAllText(path) != expected)
                throw new InvalidOperationException("scanner_start_menu_conflict");
            File.Move(temporary, path, overwrite: expected is not null);
        }
        finally
        {
            if (created && File.Exists(temporary)) { FileSystemSafety.AssertNoReparsePoints(temporary); File.Delete(temporary); }
        }
    }
    public static void RollbackCreated(string root, string origin, bool testMode, bool created)
        => Rollback(root, origin, testMode, new(created));
    public static void Rollback(string root, string origin, bool testMode, ScannerShortcutChange? change)
    {
        if (change is null || (!change.Created && change.PreviousContents is null)) return;
        var path = PathFor(root, testMode);
        try { FileSystemSafety.AssertNoReparsePoints(path); }
        catch (InvalidOperationException error) when (error.Message == "managed_path_is_link") { return; }
        if (!File.Exists(path) || File.ReadAllText(path) != Contents(root, origin)) return;
        if (change.PreviousContents is not null) WriteAtomic(path, change.PreviousContents, Contents(root, origin));
        else if (change.Created) File.Delete(path);
    }
    public static void RemoveOwned(string root, bool testMode)
    {
        var path = PathFor(root, testMode);
        try { FileSystemSafety.AssertNoReparsePoints(path); }
        catch (InvalidOperationException error) when (error.Message == "managed_path_is_link") { return; }
        if (File.Exists(path) && ScannerConstants.AllowedOrigins.Any(origin =>
            File.ReadAllText(path) == Contents(root, origin) || File.ReadAllText(path) == LegacyContents(root, origin))) File.Delete(path);
    }
}
