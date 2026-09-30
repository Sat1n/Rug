using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static class NativeInputTakeoverTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        { Console.WriteLine("Native input takeover: NOT RUN (Windows desktop required)"); return; }
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.slnx"))) root = root.Parent;
        string? dll = root is null ? null : Path.Combine(root.FullName, "x64", "Debug", "Rug.Core.dll");
        if (dll is null || !File.Exists(dll))
        { Console.WriteLine("Native input takeover: NOT RUN (Debug x64 Rug.Core.dll unavailable)"); return; }
        var assembly = typeof(InputService).Assembly;
        nint library = NativeLibrary.Load(dll, assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "Rug.Core.dll" ? library : 0);
        using var window = new IsolatedWindow();
        nint hwnd = await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var arbiter = new InputArbiter();
        await using var native = new InputService();
        var manifest = new PluginManifest([Permission.ControlInput]) { Id = "native-takeover", PluginDirectory = AppContext.BaseDirectory };
        await using var bridge = new InputBridge(native, TestAuthorizationHost.Gate(manifest, NullLogger.Instance), arbiter: arbiter);
        await bridge.BindAsync(hwnd, InputDeliveryMode.Win32PostMessage);
        await bridge.ClickAsync(20, 20);
        await window.MouseDown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await window.MouseUp.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var key = bridge.PressKeyAsync(65, 60000);
        await window.KeyDown.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var takeover = arbiter.TakeOverAsync();
        try { await key; throw new Exception("Native held key was not cancelled"); } catch (OperationCanceledException) { }
        await takeover.WaitAsync(TimeSpan.FromSeconds(3));
        await window.KeyUp.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (arbiter.Snapshot.State != InputControlState.ManualControl) throw new Exception("Native cleanup did not finish manual control");
        arbiter.ReturnControl();
        Console.WriteLine("Native PostMessage isolated-window click/key release and manual takeover passed");
        // Keep the loaded module for the process lifetime; InputService's generated imports cache pointers.
    }

    private sealed class IsolatedWindow : IDisposable
    {
        public readonly TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource MouseDown = new(TaskCreationOptions.RunContinuationsAsynchronously), MouseUp = new(TaskCreationOptions.RunContinuationsAsynchronously),
            KeyDown = new(TaskCreationOptions.RunContinuationsAsynchronously), KeyUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private readonly WindowProc _proc;
        private nint _hwnd;
        public IsolatedWindow()
        {
            _proc = HandleMessage;
            _thread = new Thread(Run) { IsBackground = true, Name = "Rug isolated input test window" };
            _thread.Start();
        }
        private void Run()
        {
            string name = "RugInputTest" + Guid.NewGuid().ToString("N");
            try
            {
                var wc = new WindowClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), ClassName = name };
                if (RegisterClassW(ref wc) == 0) throw new Exception("Could not register isolated window");
                _hwnd = CreateWindowExW(0, name, name, 0x00CF0000, 0, 0, 300, 200, 0, 0, 0, 0);
                if (_hwnd == 0) throw new Exception("Could not create isolated input window");
                Ready.SetResult(_hwnd);
                while (GetMessageW(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessageW(ref message); }
            }
            catch (Exception error) { Ready.TrySetException(error); }
            finally { if (_hwnd != 0) DestroyWindow(_hwnd); UnregisterClassW(name, 0); }
        }
        private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            if (message == 0x201) MouseDown.TrySetResult();
            if (message == 0x202) MouseUp.TrySetResult();
            if (message == 0x100) KeyDown.TrySetResult();
            if (message == 0x101) KeyUp.TrySetResult();
            if (message == 0x2) PostQuitMessage(0);
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
        public void Dispose()
        {
            if (_hwnd != 0) PostMessageW(_hwnd, 0x10, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(3))) throw new Exception("Isolated test window did not exit");
            GC.KeepAlive(_proc);
        }
        private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public nint Proc;
            public int ClassExtra, WindowExtra;
            public nint Instance, Icon, Cursor, Background;
            public string? MenuName, ClassName;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Message { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WindowClass windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
        [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, nint instance);
    }
}
