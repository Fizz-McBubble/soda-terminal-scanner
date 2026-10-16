using System.Runtime.InteropServices;
using System.Text;

namespace Soda.Scanner.Core;

public sealed record ScannerShortcutChange(bool Created, string? PreviousContents = null);

public static class ScannerShortcut
{
    public const string Name = "Soda Terminal · 画面扫描.lnk";
    public const string OldName = "Soda Terminal · 画面扫描.url";
    private const string Description = "Soda Terminal · 画面扫描";
    public static string PathFor(string root, bool testMode) => EntryPath(root, testMode, Name);
    public static string OldPathFor(string root, bool testMode) => EntryPath(root, testMode, OldName);
    private static string EntryPath(string root, bool testMode, string name) => testMode
        ? FileSystemSafety.SafePath(root, name)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), name);
    public static string Contents(string root, string origin) => "[InternetShortcut]\r\nURL=" + origin +
        "/system/scanner\r\nIconFile=" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\r\nIconIndex=0\r\n";
    public static string LegacyContents(string root, string origin) => "[InternetShortcut]\r\nURL=soda-terminal-scanner://open?origin=" +
        Uri.EscapeDataString(origin) + "\r\nIconFile=" + Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + "\r\nIconIndex=0\r\n";
    private static string Arguments(string origin) => "--open-workspace " + origin;
    public static bool IsOwned(string root, string origin, bool testMode)
    {
        var path = PathFor(root, testMode);
        FileSystemSafety.AssertNoReparsePoints(path);
        if (!File.Exists(path)) return false;
        try
        {
            return WithShortcut<bool>(path, link => string.Equals((string)link.TargetPath,
                    Path.Combine(Path.GetFullPath(root), ScannerConstants.HelperExecutableRelative), StringComparison.OrdinalIgnoreCase)
                && (string)link.Arguments == Arguments(origin)
                && (string)link.WorkingDirectory == Path.Combine(Path.GetFullPath(root), "helper")
                && (string)link.IconLocation == Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + ",0"
                && (string)link.Description == Description);
        }
        catch (COMException) { return false; }
    }
    private static T WithShortcut<T>(string path, Func<dynamic, T> operation)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try { shortcut = ((dynamic)shell).CreateShortcut(path); return operation(shortcut); }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
    public static bool Create(string root, string origin, bool testMode) => Apply(root, origin, testMode).Created;
    public static ScannerShortcutChange Apply(string root, string origin, bool testMode)
    {
        if (!ScannerConstants.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) throw new InvalidOperationException("scanner_public_origin_invalid");
        var path = PathFor(root, testMode); var oldPath = OldPathFor(root, testMode);
        FileSystemSafety.AssertNoReparsePoints(path); FileSystemSafety.AssertNoReparsePoints(oldPath);
        if (File.Exists(path) && !IsOwned(root, origin, testMode)) throw new InvalidOperationException("scanner_start_menu_conflict");
        var previous = File.Exists(oldPath) ? File.ReadAllText(oldPath) : null;
        if (previous != null && previous != Contents(root, origin) && previous != LegacyContents(root, origin))
            throw new InvalidOperationException("scanner_start_menu_conflict");
        var created = !File.Exists(path);
        if (created)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".tmp-{Guid.NewGuid():N}"[..12] + ".lnk");
            try
            {
                WithShortcut<bool>(temporary, link =>
                {
                    link.TargetPath = Path.Combine(Path.GetFullPath(root), ScannerConstants.HelperExecutableRelative);
                    link.Arguments = Arguments(origin); link.WorkingDirectory = Path.Combine(Path.GetFullPath(root), "helper");
                    link.IconLocation = Path.Combine(Path.GetFullPath(root), ScannerConstants.UninstallExecutableName) + ",0";
                    link.Description = Description; link.Save(); return true;
                });
                FileSystemSafety.AssertNoReparsePoints(path);
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) { FileSystemSafety.AssertNoReparsePoints(temporary); File.Delete(temporary); } }
        }
        if (previous != null)
        {
            try
            {
                FileSystemSafety.AssertNoReparsePoints(oldPath);
                if (File.ReadAllText(oldPath) != previous) throw new InvalidOperationException("scanner_start_menu_conflict");
                File.Delete(oldPath);
            }
            catch { if (created && IsOwned(root, origin, testMode)) File.Delete(path); throw; }
        }
        return new(created, previous);
    }
    public static void RollbackCreated(string root, string origin, bool testMode, bool created)
        => Rollback(root, origin, testMode, new(created));
    public static void Rollback(string root, string origin, bool testMode, ScannerShortcutChange? change)
    {
        if (change is null) return;
        try
        {
            if (!IsOwned(root, origin, testMode)) return;
            var oldPath = OldPathFor(root, testMode); FileSystemSafety.AssertNoReparsePoints(oldPath);
            if (change.PreviousContents != null && !File.Exists(oldPath))
            {
                using var stream = new FileStream(oldPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UnicodeEncoding(false, true)); writer.Write(change.PreviousContents);
            }
            if (change.Created) File.Delete(PathFor(root, testMode));
        }
        catch (InvalidOperationException error) when (error.Message == "managed_path_is_link") { }
    }
    public static void RemoveOwned(string root, bool testMode)
    {
        try
        {
            if (ScannerConstants.AllowedOrigins.Any(origin => IsOwned(root, origin, testMode))) File.Delete(PathFor(root, testMode));
            var oldPath = OldPathFor(root, testMode); FileSystemSafety.AssertNoReparsePoints(oldPath);
            if (File.Exists(oldPath) && ScannerConstants.AllowedOrigins.Any(origin =>
                File.ReadAllText(oldPath) == Contents(root, origin) || File.ReadAllText(oldPath) == LegacyContents(root, origin))) File.Delete(oldPath);
        }
        catch (InvalidOperationException error) when (error.Message == "managed_path_is_link") { }
    }
}
