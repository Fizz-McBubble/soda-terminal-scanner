using Soda.Scanner.Setup;

internal static class PayloadFilesTests
{
    internal static void Run(string root, Action<string, Action> test)
    {
        var directory = Path.Combine(root, "payload-handoff");
        Directory.CreateDirectory(directory);
        void Refuses(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new Exception("invalid payload handoff accepted");
        }
        test("payload wait has explicit parent failure and bounded timeout", () =>
        {
            Refuses(() => PayloadFiles.Wait(directory, () => false, TimeSpan.FromSeconds(1)));
            Refuses(() => PayloadFiles.Wait(directory, () => true, TimeSpan.Zero));
            File.WriteAllText(Path.Combine(directory, "failed"), "");
            Refuses(() => PayloadFiles.Wait(directory, () => true, TimeSpan.FromSeconds(1)));
        });
        test("payload readiness and file lengths are strict and stream excludes mutation", () =>
        {
            var validDirectory = Path.Combine(root, "payload-ready");
            Directory.CreateDirectory(validDirectory);
            File.WriteAllText(Path.Combine(validDirectory, "ready.tmp"), PayloadFiles.ReadyContents);
            Refuses(() => PayloadFiles.Wait(validDirectory, () => true, TimeSpan.Zero));
            File.WriteAllText(Path.Combine(validDirectory, "ready"), "wrong");
            Refuses(() => PayloadFiles.Wait(validDirectory, () => true, TimeSpan.Zero));
            File.WriteAllText(Path.Combine(validDirectory, "ready"), PayloadFiles.ReadyContents);
            PayloadFiles.Wait(validDirectory, () => true, TimeSpan.Zero);
            File.WriteAllBytes(Path.Combine(validDirectory, "item"), [1, 2, 3]);
            Refuses(() => { using var bad = PayloadFiles.OpenChecked(validDirectory, "item", long.MaxValue); });
            using var stream = PayloadFiles.OpenChecked(validDirectory, "item", 3);
            try { using var writer = File.OpenWrite(Path.Combine(validDirectory, "item")); }
            catch (IOException) { return; }
            throw new Exception("payload stream allowed mutation");
        });
    }
}
