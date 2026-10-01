using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace CsServerWatch;

public sealed partial class MainWindow : Window
{
    private readonly SubclassProc _subclass;

    public MainWindow()
    {
        InitializeComponent();
        Title = "CS Server Watch";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _subclass = OnSubclass;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowSubclass(hwnd, _subclass, 1, UIntPtr.Zero);

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
        RootFrame.Navigate(typeof(MainPage));
    }

    private IntPtr OnSubclass(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == 0x0024)
        {
            var result = DefSubclassProc(hwnd, msg, wParam, lParam);
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                dpi = 96;
            }

            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(980 * dpi / 96.0));
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(680 * dpi / 96.0));
            Marshal.StructureToPtr(info, lParam, false);
            return result;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
