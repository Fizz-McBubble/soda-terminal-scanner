namespace ZZZScannerHelper;

internal static class HelperLog
{
    private static readonly object Sync = new();

    public static string DirectoryPath => Path.Combine(HelperStorageManager.DefaultDataRoot(), "logs");

    public static string RecordException(string code, string phase, Exception exception)
    {
        var id = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]).ToLowerInvariant();
        Write($"ERROR id={id} code={code} phase={phase} type={exception.GetType().Name} message={exception.Message}\n{exception}");
        return id;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, $"helper-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                foreach (var stale in new DirectoryInfo(DirectoryPath)
                    .EnumerateFiles("helper-*.log")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(7))
                {
                    try { stale.Delete(); } catch { }
                }
            }
        }
        catch
        {
        }
    }
}
