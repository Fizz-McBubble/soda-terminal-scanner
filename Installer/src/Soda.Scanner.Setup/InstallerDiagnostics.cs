using System.Diagnostics;
using Soda.Scanner.Core;

namespace Soda.Scanner.Setup;

internal sealed class InstallerDiagnostics
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private string _stage = "prepare";
    internal void Phase(string stage) { if (InstallerFeedback.Stages.Contains(stage)) _stage = stage; }
    internal InstallerFeedbackReport Failure(Exception error) => InstallerFeedback.Create(_stage, InstallerFeedback.Classify(error), _watch.ElapsedMilliseconds);
    internal static bool Save(string root, InstallerFeedbackReport report)
    {
        try
        {
            var path = FileSystemSafety.SafePath(root, "installer-diagnostics/latest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileSystemSafety.AssertNoReparsePoints(path);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(InstallerFeedback.Serialize(report));
            return true;
        }
        catch { return false; }
    }
}
