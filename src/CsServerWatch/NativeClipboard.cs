using System.Runtime.InteropServices;
using System.Text;

namespace CsServerWatch;

internal static class NativeClipboard
{
    public static void SetText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("The clipboard is busy.");
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("The clipboard could not be cleared.");
            }

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not allocate clipboard text.");
            }

            var pointer = GlobalLock(handle);
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(13, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("Could not put text on the clipboard.");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
