#nullable enable
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Native;

internal sealed class RawInputKeyboardTracker
{
    private readonly HashSet<(ulong Device, ushort Key, ushort Scan, ushort Extended)> _pressed = new();
    internal InputObservation Apply(InputObservation observation)
    {
        if (observation.Kind == InputEventKind.DeviceRemoved) _pressed.RemoveWhere(k => k.Device == observation.DeviceId);
        if (observation.Kind != InputEventKind.Keyboard) return observation;
        var key = (observation.DeviceId, observation.VirtualKey, observation.ScanCode, (ushort)(observation.KeyFlags & 6));
        bool repeat = !observation.KeyReleased && !_pressed.Add(key);
        if (observation.KeyReleased) _pressed.Remove(key);
        if (_pressed.Count > 4096) throw new RawInputException(InputEventSourceFault.Overflow, "Keyboard tracking capacity exceeded.");
        return observation with { IsRepeat = repeat };
    }
    internal void Clear() => _pressed.Clear();
}

// Native device handles never leave the receiver; subscribers receive session-local IDs.
internal sealed class RawInputDeviceIds
{
    private readonly Dictionary<ulong, ulong> _ids = new();
    private ulong _next;
    internal ulong Get(ulong handle)
    {
        if (handle == 0) return 0; // Unattributed, not automatically software input.
        if (_ids.TryGetValue(handle, out var id)) return id;
        if (_ids.Count >= 4096) throw new RawInputException(InputEventSourceFault.Overflow, "Device identity capacity exceeded.");
        _ids.Add(handle, id = ++_next); return id;
    }
    internal void Remove(ulong handle) => _ids.Remove(handle);
    internal void Clear() { _ids.Clear(); _next = 0; }
}
