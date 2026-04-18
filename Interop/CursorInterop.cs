using System.Runtime.InteropServices;

namespace RightSpeak.Interop;

internal static class CursorInterop
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var point))
        {
            x = point.X;
            y = point.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
