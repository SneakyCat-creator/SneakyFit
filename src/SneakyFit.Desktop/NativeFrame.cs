using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SneakyFit.Desktop;

internal static class NativeFrame
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || SystemParameters.HighContrast) return;
        var dark = 0; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var caption = 0x00FCF3F5; DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        var text = 0x003F2229; DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        var corners = 2; DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        var edge = 0x00EBB2C8; DwmSetWindowAttribute(handle, 34, ref edge, sizeof(int));
    }
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
