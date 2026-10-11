using System.Runtime.InteropServices;
using System.Text;

namespace ZZZScannerNext.Interop;

internal static partial class NativeMethods
{
    private delegate bool EnumerateWindowCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumerateWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumerateWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder text, int maximum);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint ReadWindowProcessId(IntPtr handle, out uint processId);

    internal static int WindowProcessId(IntPtr handle)
    {
        ReadWindowProcessId(handle, out var processId);
        return checked((int)processId);
    }

    internal static IReadOnlyList<IntPtr> EnumerateWindows(IntPtr parent = default)
    {
        var handles = new List<IntPtr>();
        bool Add(IntPtr handle, IntPtr _) { handles.Add(handle); return true; }
        if (parent == IntPtr.Zero) EnumWindows(Add, IntPtr.Zero);
        else EnumChildWindows(parent, Add, IntPtr.Zero);
        return handles;
    }

    internal static string WindowText(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    internal static string WindowClass(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }
}
