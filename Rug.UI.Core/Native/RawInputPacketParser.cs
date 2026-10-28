#nullable enable
using System.Buffers.Binary;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Native;

internal static class RawInputPacketParser
{
    /// <summary>@shape packet: complete copied RAWINPUT, pointerSize: 4 or 8; @source layout: Win32 RAWINPUT/RAWKEYBOARD/RAWMOUSE</summary>
    internal static InputObservation? Parse(ReadOnlySpan<byte> packet, bool foreground, int pointerSize)
    {
        int header = pointerSize is 4 or 8 ? 8 + pointerSize * 2 : throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (packet.Length < header || BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]) != packet.Length)
            throw Invalid("Truncated or inconsistent RAWINPUT header.");
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        ulong device = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(packet[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]);
        var data = packet[header..];
        if (type == 1)
        {
            if (data.Length < 16) throw Invalid("Truncated RAWKEYBOARD.");
            ushort scan = U16(data), flags = U16(data[2..]), key = U16(data[6..]);
            uint message = U32(data[8..]);
            if (scan == 0xff) throw Invalid("Keyboard overrun; input continuity lost.");
            if (key == 255) return null; // Windows documents this as an unmapped virtual key.
            if (key is 0 or > 255 || (flags & ~7) != 0 || U16(data[4..]) != 0
                || message is not (0x100 or 0x101 or 0x104 or 0x105)
                || ((flags & 1) != 0) != (message is 0x101 or 0x105)) throw Invalid("Invalid RAWKEYBOARD values.");
            return new(InputEventKind.Keyboard, device, foreground, key, scan, flags, message, ExtraInformation: U32(data[12..]));
        }
        if (type == 0)
        {
            if (data.Length < 24) throw Invalid("Truncated RAWMOUSE.");
            ushort flags = U16(data), buttons = U16(data[4..]);
            if ((flags & ~15) != 0 || (buttons & ~0x0fff) != 0) throw Invalid("Invalid RAWMOUSE flags.");
            return new(InputEventKind.Mouse, device, foreground, MouseFlags: flags, ButtonFlags: buttons,
                WheelDelta: (buttons & 0x0c00) != 0 ? BinaryPrimitives.ReadInt16LittleEndian(data[6..]) : (short)0,
                X: BinaryPrimitives.ReadInt32LittleEndian(data[12..]), Y: BinaryPrimitives.ReadInt32LittleEndian(data[16..]), ExtraInformation: U32(data[20..]));
        }
        throw Invalid("Unexpected raw device type.");
    }
    private static ushort U16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt16LittleEndian(data);
    private static uint U32(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static RawInputException Invalid(string message) => new(InputEventSourceFault.InvalidData, message);
}
