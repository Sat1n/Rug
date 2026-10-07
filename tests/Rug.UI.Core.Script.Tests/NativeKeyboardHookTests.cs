using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;

internal static class NativeKeyboardHookTests
{
    internal static string LibraryPath => Path.Combine(Path.GetDirectoryName(DiagnosticNativeRawInputPump.LibraryPath
        ?? throw new FileNotFoundException("Repository root not found."))!, "Rug.NativeKeyboardHook.dll");
    internal static void ParseAndOwnership()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(LibraryPath))
        { Console.WriteLine("NOT RUN: native callback parser/ownership; build tests/run_native_input_probe.ps1 -Mode Build first."); return; }
        using var native = new NativeKeyboardHookDiagnostic(LibraryPath);
        native.ObserveForTest(-1, 0, 1); native.ObserveForTest(1, 0, 1);
        RawInputTests.Check(native.Status is { CallbackTotal: 2, ActionableTotal: 0, ParseErrors: 0 }, "native non-action callback read sentinel");
        nint data = Marshal.AllocHGlobal(24);
        try
        {
            foreach (bool injected in new[] { false, true }) foreach (bool tagged in new[] { false, true })
            {
                Marshal.WriteInt32(data, 0, 0x78); // F9: intersections only, not a retained human key.
                Marshal.WriteInt32(data, 8, injected ? 0x10 : 0);
                Marshal.WriteIntPtr(data, 16, tagged ? (nint)InputObservation.RugSoftwareInputTag : 0);
                native.ObserveForTest(0, 0x100, data);
            }
            native.ObserveForTest(0, 0x100, 0);
            var before = native.Status;
            RawInputTests.Check(before is { CallbackTotal: 7, ActionableTotal: 5, Injected: 2, Unflagged: 2,
                TaggedTotal: 2, TaggedInjected: 1, TaggedUnflagged: 1, ParseErrors: 1, F8Down: 0, BDown: 0 }, "native classification intersections incorrect");
            Marshal.WriteInt32(data, 8, 0x10); Marshal.WriteIntPtr(data, 16, (nint)InputObservation.RugSoftwareInputTag);
            foreach (int key in new[] { 0x77, 0x42 }) foreach (nuint message in new nuint[] { 0x100, 0x101, 0x104, 0x105 })
            { Marshal.WriteInt32(data, 0, key); native.ObserveForTest(0, message, data); }
            RawInputTests.Check(native.Status is { F8Down: 2, F8Up: 2, BDown: 2, BUp: 2 } && before.F8Down == 0, "native software key aggregates/snapshot broken");
        }
        finally { Marshal.FreeHGlobal(data); }
        native.Attach();
        var attached = native.Status;
        try { native.Attach(); throw new Exception("duplicate native callback attachment accepted"); } catch (System.ComponentModel.Win32Exception) { }
        using (var other = new NativeKeyboardHookDiagnostic(LibraryPath))
        {
            try { other.Attach(); throw new Exception("native callback owner silently replaced"); } catch (System.ComponentModel.Win32Exception) { }
        }
        try { native.Dispose(); throw new Exception("active native callback unloaded"); } catch (System.ComponentModel.Win32Exception) { }
        RawInputTests.Check(native.Status.ThreadId == attached.ThreadId && native.Status.Attached == 1, "duplicate attachment changed owner");
        native.Detach(); native.Dispose(); native.Dispose();
        RawInputTests.Check(native.Status.Attached == 0, "native callback not detached");
        Console.WriteLine("Native keyboard callback parser/intersections/software key aggregates/owner/unload guard passed (no input or Hook chain emitted)");
    }
}
