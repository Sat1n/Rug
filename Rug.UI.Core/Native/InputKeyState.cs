#nullable enable
using System.Runtime.InteropServices;

namespace Rug.UI.Core.Native;

internal static class InputKeyState
{
    // Only current-down high bit; never the unreliable pressed-since-last-call bit.
    internal static IReadOnlyCollection<ushort> PressedKeys()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Input detection requires Windows.");
        return Enumerable.Range(1, 254).Where(key => (GetAsyncKeyState(key) & 0x8000) != 0)
            .Select(key => (ushort)key).ToArray();
    }
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
