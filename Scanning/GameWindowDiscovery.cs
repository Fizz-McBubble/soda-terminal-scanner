using System.Diagnostics;
using System.Drawing;
using ZZZScannerNext.Interop;

namespace ZZZScannerNext.Scanning;

internal readonly record struct GameWindowTarget(
    IntPtr Handle, IntPtr ContentHandle, int ProcessId, string ProcessName, bool IsCloud, bool Foreground);
internal readonly record struct GameWindowObservation(
    long Handle, long Parent, int ProcessId, string ProcessName, string Title, string Class,
    Rectangle Client, bool Visible, bool MainWindow = false);

internal static class GameWindowDiscovery
{
    internal const string LocalProcessName = "ZenlessZoneZero";
    internal const string CloudProcessName = "Zenless Zone Zero Cloud";

    // The standard entry uses "auto". Explicit process overrides never
    // silently fall back to another app, including the local client's name.
    internal static string[] ProcessNames(string requested) =>
        requested.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? [LocalProcessName, CloudProcessName, "ZenlessZoneZeroCloud"]
                : [Path.GetFileNameWithoutExtension(requested)];

    internal static bool IsCloudProcess(string name) =>
        name.Equals(CloudProcessName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("ZenlessZoneZeroCloud", StringComparison.OrdinalIgnoreCase);

    internal static GameWindowTarget Find(string requested) => Select(Observe(requested), requested);

    internal static GameWindowTarget Select(IReadOnlyList<GameWindowTarget> candidates, string requested)
    {
        var foreground = candidates.Where(candidate => candidate.Foreground).ToArray();
        if (foreground.Length == 1) return foreground[0];
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
            throw new ScannerFailureException("game_window_ambiguous", "检测到多个游戏窗口",
                "本机绝区零和云绝窗口同时打开，无法确定本次扫描对象。",
                "将要扫描的驱动仓库切到前台，再重新扫描。");
        throw new ScannerFailureException("game_process_not_found", "未找到游戏画面",
            $"未找到游戏窗口进程：{requested}（本机绝区零或云·绝区零）",
            "请进入本机绝区零或云·绝区零的驱动仓库；云绝启动器本身没有可扫描的游戏画面。");
    }

    internal static IReadOnlyList<GameWindowTarget> Observe(string requested)
    {
        var observed = Inspect(requested);
        var foreground = NativeMethods.GetForegroundWindow();
        return Candidates(observed, foreground.ToInt64(),
            NativeMethods.GetAncestor(foreground, 2).ToInt64(), NativeMethods.WindowProcessId(foreground));
    }

    internal static IReadOnlyList<GameWindowTarget> Candidates(
        IReadOnlyList<GameWindowObservation> observed, long foreground, long foregroundRoot, int foregroundProcessId)
    {
        var candidates = new List<GameWindowTarget>();
        foreach (var window in observed.Where(window => window.Parent == 0 && window.Visible))
        {
            var handle = new IntPtr(window.Handle);
            var cloud = IsCloudProcess(window.ProcessName);
            // Preserve the local client's established MainWindowHandle path;
            // settings/tool windows in that process are not scan candidates.
            if (!cloud && !window.MainWindow) continue;
            var renderers = observed.Where(child => child.Parent == window.Handle && child.Visible
                && child.ProcessId == window.ProcessId && child.Title == "RenderWindow"
                && child.Class == "Static" && child.Client.Width > 0 && child.Client.Height > 0).ToArray();
            var content = cloud ? renderers.Length == 1 ? new IntPtr(renderers[0].Handle) : IntPtr.Zero : handle;
            // Cloud launchers and overlays share the executable. A live render
            // surface owns capture geometry in all three display modes.
            if (content == IntPtr.Zero) continue;
            var target = new GameWindowTarget(handle, content, window.ProcessId, window.ProcessName, cloud, false);
            candidates.Add(target with { Foreground = IsForeground(target, foreground, foregroundRoot, foregroundProcessId) });
        }
        return candidates;
    }

    internal static bool IsForeground(GameWindowTarget target, long foreground, long foregroundRoot, int processId) =>
        foreground != 0 && processId == target.ProcessId
            && (foreground == target.Handle.ToInt64() || foregroundRoot == target.Handle.ToInt64());

    internal static bool IsForeground(GameWindowTarget target)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        // GA_ROOT follows child ancestry only. GA_ROOTOWNER would incorrectly
        // accept cloud settings/help/exit overlays owned by the game window.
        return IsForeground(target, foreground.ToInt64(), NativeMethods.GetAncestor(foreground, 2).ToInt64(),
            NativeMethods.WindowProcessId(foreground));
    }

    internal static bool ContentIdentityMatches(GameWindowTarget target, int rootProcessId, int contentProcessId,
        long contentRoot, string title, string windowClass) =>
        rootProcessId == target.ProcessId && contentProcessId == target.ProcessId
            && contentRoot == target.Handle.ToInt64()
            && (!target.IsCloud || title == "RenderWindow" && windowClass == "Static");

    internal static bool IsCurrent(GameWindowTarget target) =>
        NativeMethods.IsWindow(target.Handle) && NativeMethods.IsWindow(target.ContentHandle)
            && ContentIdentityMatches(target, NativeMethods.WindowProcessId(target.Handle),
                NativeMethods.WindowProcessId(target.ContentHandle), NativeMethods.GetAncestor(target.ContentHandle, 2).ToInt64(),
                target.IsCloud ? NativeMethods.WindowText(target.ContentHandle) : "",
                target.IsCloud ? NativeMethods.WindowClass(target.ContentHandle) : "");

    // A bounded, read-only inspection used by the live regression probe. It
    // records no unrelated process metadata or game/account contents.
    internal static IReadOnlyList<GameWindowObservation> Inspect(string requested)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        var processes = new Dictionary<int, (string Name, IntPtr MainWindow)>();
        foreach (var name in ProcessNames(requested))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { processes[process.Id] = (process.ProcessName, process.MainWindowHandle); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        var observations = new List<GameWindowObservation>();
        foreach (var handle in NativeMethods.EnumerateWindows())
        {
            var processId = NativeMethods.WindowProcessId(handle);
            if (!processes.TryGetValue(processId, out var process)) continue;
            Add(handle, IntPtr.Zero, processId, process.Name, handle == process.MainWindow);
            foreach (var child in NativeMethods.EnumerateWindows(handle))
                if (NativeMethods.WindowProcessId(child) == processId && NativeMethods.GetAncestor(child, 2) == handle)
                    Add(child, handle, processId, process.Name, false);
        }
        return observations;
        void Add(IntPtr handle, IntPtr parent, int processId, string name, bool mainWindow)
        {
            try
            {
                observations.Add(new(handle.ToInt64(), parent.ToInt64(), processId, name,
                    NativeMethods.WindowText(handle), NativeMethods.WindowClass(handle),
                    ReadClientRectangle(handle), NativeMethods.IsWindowVisible(handle), mainWindow));
            }
            catch (InvalidOperationException) { } // Window closed during enumeration.
        }
    }

    internal static Rectangle ReadClientRectangle(IntPtr handle)
    {
        if (!NativeMethods.GetClientRect(handle, out var nativeRect))
            throw new InvalidOperationException("无法读取游戏客户区。");
        Rectangle client = nativeRect;
        var origin = new NativePoint { X = client.X, Y = client.Y };
        if (!NativeMethods.ClientToScreen(handle, ref origin))
            throw new InvalidOperationException("无法换算游戏窗口坐标。");
        return new(origin.X, origin.Y, client.Width, client.Height);
    }
}
