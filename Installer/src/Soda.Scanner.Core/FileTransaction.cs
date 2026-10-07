namespace Soda.Scanner.Core;

internal sealed class FileTransaction(string root, string work) : IDisposable
{
    private sealed record Change(string Target, string? Backup, bool Preserve);
    private readonly List<Change> changes = new();
    private bool complete;

    public void Copy(string source, string target, bool preserveReplaced = false)
    {
        FileSystemSafety.AssertSafePath(root, source);
        FileSystemSafety.AssertSafePath(root, target);
        if (File.Exists(target) && new FileInfo(source).Length == new FileInfo(target).Length &&
            Sha256Util.ComputeFileSha256(source) == Sha256Util.ComputeFileSha256(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? backup = null;
        if (File.Exists(target))
        {
            backup = FileSystemSafety.SafePath(work, "backup\\" + changes.Count);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(target, backup);
        }
        changes.Add(new Change(target, backup, preserveReplaced));
        var temporary = FileSystemSafety.SafePath(root, Path.GetRelativePath(root, target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.Copy(source, temporary, overwrite: false); File.Move(temporary, target); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Commit()
    {
        var preservationRoot = FileSystemSafety.SafePath(root, "preserved-components\\" + Path.GetFileName(work));
        foreach (var change in changes.Where(change => change.Preserve && change.Backup != null))
        {
            var saved = FileSystemSafety.SafePath(preservationRoot, Path.GetRelativePath(root, change.Target));
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.Copy(change.Backup!, saved, overwrite: false);
        }
        complete = true;
    }

    public void Rollback()
    {
        if (complete) return;
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            FileSystemSafety.AssertSafePath(root, change.Target);
            if (File.Exists(change.Target)) File.Delete(change.Target);
            if (change.Backup != null)
            {
                FileSystemSafety.AssertSafePath(root, change.Backup);
                File.Move(change.Backup, change.Target);
            }
        }
        complete = true;
    }

    public void Dispose() { if (!complete) Rollback(); }
}
