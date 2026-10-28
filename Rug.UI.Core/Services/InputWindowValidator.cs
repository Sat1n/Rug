#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

namespace Rug.UI.Core.Services;

/// <summary>Window properties disappear on destruction, including when HWND is reused.</summary>
public sealed class InputWindowValidator : IInputWindowValidator
{
    private static readonly object Gate = new();
    // One property per actual window, even across host restarts; no growing registry.
    private const string Property = "Rug.Input.WindowLifetime.v1";
    private static long _stamp = Random.Shared.NextInt64(1, long.MaxValue / 2);
    public InputTarget Bind(nint hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) throw new PlatformNotSupportedException();
        lock (Gate)
        {
            if (WindowNative.GetWindowThreadProcessId(hwnd, out _) == 0) throw new ArgumentException("Invalid input window.");
            nint value = WindowNative.GetPropW(hwnd, Property);
            if (value == 0)
            {
                value = (nint)checked(++_stamp);
                if (!WindowNative.SetPropW(hwnd, Property, value))
                    throw new InputUnavailableException("Cannot verify target lifetime (window property access denied).");
            }
            return new(hwnd, (long)value);
        }
    }
    public bool IsValid(InputTarget target) => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
        target.Hwnd != 0 && WindowNative.GetWindowThreadProcessId(target.Hwnd, out _) != 0 &&
        (long)WindowNative.GetPropW(target.Hwnd, Property) == target.WindowStamp;
}
