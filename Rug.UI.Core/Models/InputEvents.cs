#nullable enable
namespace Rug.UI.Core.Models;

public enum InputEventSourceState { Uninitialized, Starting, Ready, Faulted, Stopped }
public enum InputEventKind { Keyboard, Mouse, DeviceArrived, DeviceRemoved }
public enum InputEventSourceFault { None, Registration, NativeFailure, InvalidData, Overflow, OwnershipLost, CleanupFailure }

/// <summary>Detached device observation, not an authenticated human identity. DeviceId is opaque.</summary>
public sealed record InputObservation(InputEventKind Kind, ulong DeviceId, bool Foreground,
    ushort VirtualKey = 0, ushort ScanCode = 0, ushort KeyFlags = 0, uint KeyMessage = 0,
    bool IsRepeat = false, ushort MouseFlags = 0, ushort ButtonFlags = 0, short WheelDelta = 0,
    long X = 0, long Y = 0, uint ExtraInformation = 0)
{
    public bool KeyReleased => (KeyFlags & 1) != 0;
    // Matches native Win32InputController emitters; spoofable hint, not authenticated origin.
    public const uint RugSoftwareInputTag = 0x52554749;
    public bool IsRugSoftwareInput => (Kind is InputEventKind.Keyboard or InputEventKind.Mouse) && ExtraInformation == RugSoftwareInputTag;
    public bool IsCandidateInput => (Kind is InputEventKind.Keyboard or InputEventKind.Mouse) && !IsRugSoftwareInput;
    public bool IsPureMovement => Kind == InputEventKind.Mouse && ButtonFlags == 0 && (MouseFlags & 4) == 0;
}

/// <summary>Stopwatch ticks, sequence within generation; merged motion uses latest sequence/time.</summary>
public sealed record InputSourceEvent(long Generation, long Sequence, long MonotonicTicks,
    InputObservation Observation, long Samples = 1, long FirstSequence = 0, bool HasMovement = false,
    long MovementSequence = 0, long MovementTicks = 0);

/// <summary>Receiver/consumer barrier, not Hook-health evidence.</summary>
public sealed record InputEventBoundary(long Generation, long Sequence, long MonotonicTicks);

/// <summary>Counts are per session, never contain input text, key sequences or trajectories.</summary>
public sealed record InputEventSourceSnapshot(InputEventSourceState State, long Generation, long Revision,
    InputEventSourceFault Fault, string? Error, long Keyboard, long Mouse, long DeviceChanges,
    long Coalesced, long Overflows, long ConsumerErrors, int Buffered, int Subscribers,
    long LastSequence = 0);
