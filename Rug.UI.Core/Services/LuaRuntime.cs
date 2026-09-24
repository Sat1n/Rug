#nullable enable
using System.Globalization;
using Microsoft.Extensions.Logging;
using NLua;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>
/// Serializes access to one Lua state. Lua yields an operation descriptor; its .NET
/// task runs without holding the Lua gate and a later ResumeAsync supplies the result.
/// </summary>
public sealed class LuaRuntime : IScriptRuntime
{
    private const string Bridge = """
        rug = {}
        function rug.sleep(ms) return coroutine.yield('__rug', 'sleep', ms) end
        function rug.capture() return coroutine.yield('__rug', 'capture') end
        function rug.ocr(engine_type) return coroutine.yield('__rug', 'ocr', engine_type) end
        function rug.click(x, y) return coroutine.yield('__rug', 'click', x, y) end
        function rug.press_key(key) return coroutine.yield('__rug', 'press_key', key) end
        function rug.get_config(key) return __rug_get_config(key) end
        function rug.log(level, message)
          if message == nil then message, level = level, 'info' end
          return __rug_log(level, message)
        end
        function __rug_drive(value)
          local r = table.pack(coroutine.resume(__rug_thread, value))
          return coroutine.status(__rug_thread), r
        end
        """;

    private readonly ICaptureService _capture;
    private readonly IOcrService _ocr;
    private readonly IInputService _input;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<Guid, Task<object?>> _operations = new();
    private CancellationTokenSource? _lifetime;
    private PluginManifest? _manifest;
    private Lua? _lua;
    private LuaFunction? _drive;
    private CapturedFrame? _lastFrame;
    private Guid? _pendingId;
    private ScriptState _state = ScriptState.Uninitialized;
    private volatile bool _paused;
    private bool _disposed;

    public LuaRuntime(ICaptureService capture, IOcrService ocr, IInputService input, ILogger<LuaRuntime> logger)
    {
        _capture = capture;
        _ocr = ocr;
        _input = input;
        _logger = logger;
    }

    public ScriptState State { get { lock (_stateGate) return _state; } }
    public event EventHandler<ScriptStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(string scriptPath, PluginManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("Runtime is already initialized.");
        string source = await File.ReadAllTextAsync(scriptPath, ct).ConfigureAwait(false);
        if (manifest.Permissions.Contains("input"))
        {
            if (manifest.TargetWindow == 0)
                throw new ArgumentException("Input permission requires a bound target window.", nameof(manifest));
            await _input.SetTargetAsync(manifest.TargetWindow, manifest.BackgroundInput, ct).ConfigureAwait(false);
        }
        _manifest = manifest;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _lua = new Lua();
                _lua.RegisterFunction("__rug_get_config", this, GetConfigMethod);
                _lua.RegisterFunction("__rug_log", this, LogMethod);
                _lua.DoString(Bridge);
                _lua["__rug_source"] = source;
                _lua.DoString("local chunk = assert(load(__rug_source, '@plugin', 't', _ENV)); __rug_thread = coroutine.create(function() chunk(); if type(on_tick) == 'function' then on_tick() end end); __rug_source = nil");
                // Remove ambient file/process and CLR access from script globals.
                _lua.DoString("io=nil; os=nil; package=nil; require=nil; dofile=nil; loadfile=nil; load=nil; debug=nil; luanet=nil; import=nil; load_assembly=nil");
                _drive = (LuaFunction)_lua["__rug_drive"]!;
            }, ct).ConfigureAwait(false);
            ChangeState(ScriptState.Ready);
        }
        catch
        {
            ChangeState(ScriptState.Faulted);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static readonly System.Reflection.MethodInfo GetConfigMethod = typeof(LuaRuntime).GetMethod(nameof(GetConfig))!;
    private static readonly System.Reflection.MethodInfo LogMethod = typeof(LuaRuntime).GetMethod(nameof(Log))!;

    public string? GetConfig(string key) => _manifest?.Configuration?.GetValueOrDefault(key);

    public void Log(string level, string message)
    {
        LogLevel severity = level.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace, "debug" => LogLevel.Debug, "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error, "critical" => LogLevel.Critical, _ => LogLevel.Information
        };
        _logger.Log(severity, "Lua: {Message}", message);
    }

    public Task<ScriptExecutionResult> StepAsync() => DriveAsync(false);
    public Task<ScriptExecutionResult> ResumeAsync() => DriveAsync(true);

    private async Task<ScriptExecutionResult> DriveAsync(bool resume)
    {
        await _driveGate.WaitAsync().ConfigureAwait(false);
        try { return await DriveCoreAsync(resume).ConfigureAwait(false); }
        finally { _driveGate.Release(); }
    }

    private async Task<ScriptExecutionResult> DriveCoreAsync(bool resume)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LuaRuntime));
        if (State == ScriptState.Stopped) return new(ScriptState.Stopped);
        if (_paused) return new(ScriptState.Paused);
        if (resume != (_pendingId is not null))
            throw new InvalidOperationException(resume ? "No yielded operation to resume." : "Use ResumeAsync for a yielded operation.");
        if (!resume && State != ScriptState.Ready) throw new InvalidOperationException("Runtime is not ready.");
        CancellationToken token = _lifetime?.Token ?? throw new InvalidOperationException("Runtime is not initialized.");
        object? input = null;
        Guid? completedId = null;
        if (resume)
        {
            Guid id = _pendingId!.Value;
            try { input = await _operations[id].WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Stop();
                return new(ScriptState.Stopped);
            }
            catch (Exception ex)
            {
                ChangeState(ScriptState.Faulted);
                return new(ScriptState.Faulted, Error: ex);
            }
            completedId = id;
        }
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (State == ScriptState.Stopped) return new(ScriptState.Stopped);
                if (_paused) return new(ScriptState.Paused);
                if (completedId is Guid id)
                {
                    _operations.Remove(id);
                    _pendingId = null;
                }
                ChangeState(ScriptState.Running);
                return await Task.Run(() => ResumeLua(input, token), token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Stop();
            return new(ScriptState.Stopped);
        }
        catch (Exception ex)
        {
            ChangeState(ScriptState.Faulted);
            return new(ScriptState.Faulted, Error: ex);
        }
    }

    private ScriptExecutionResult ResumeLua(object? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        input = ToLuaValue(input);
        object[] result = _drive!.Call(new object?[] { input }) ?? throw new InvalidOperationException("Lua returned no result.");
        string status = Convert.ToString(result[0], CultureInfo.InvariantCulture)!;
        var values = (LuaTable)result[1];
        if (values[1] is not true)
            throw new InvalidOperationException(Convert.ToString(values[2], CultureInfo.InvariantCulture));
        if (status == "dead")
        {
            ChangeState(ScriptState.Stopped);
            return new(ScriptState.Stopped, values[2]);
        }
        if (!Equals(values[2], "__rug"))
            throw new InvalidOperationException("Unexpected Lua yield; use rug.* asynchronous APIs.");
        string operation = Convert.ToString(values[3], CultureInfo.InvariantCulture)!;
        Task<object?> task = BeginOperation(operation, values, token);
        Guid id = Guid.NewGuid();
        _operations.Add(id, task);
        _pendingId = id;
        ChangeState(ScriptState.Yielded);
        return new(State);
    }

    private Task<object?> BeginOperation(string operation, LuaTable args, CancellationToken ct)
    {
        string permission = operation switch
        {
            "sleep" => "timer", "capture" => "vision.capture", "ocr" => "vision.ocr",
            "click" or "press_key" => "input", _ => throw new InvalidOperationException($"Unknown rug operation: {operation}")
        };
        if (!_manifest!.Permissions.Contains(permission))
            throw new UnauthorizedAccessException($"Plugin permission denied: {permission}");
        return operation switch
        {
            "sleep" => SleepAsync(Int(args[4]), ct),
            "capture" => CaptureAsync(ct),
            "ocr" => OcrAsync(Convert.ToString(args[4], CultureInfo.InvariantCulture), ct),
            "click" => ClickAsync(Int(args[4]), Int(args[5]), ct),
            "press_key" => PressKeyAsync(Int(args[4]), ct),
            _ => throw new InvalidOperationException()
        };
    }

    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static async Task<object?> SleepAsync(int ms, CancellationToken ct)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms));
        await Task.Delay(ms, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<object?> CaptureAsync(CancellationToken ct)
    {
        _lastFrame = await _capture.GrabFrameAsync(ct).ConfigureAwait(false);
        return _lastFrame;
    }

    private async Task<object?> OcrAsync(string? engine, CancellationToken ct)
    {
        if (_lastFrame is null) throw new InvalidOperationException("Call rug.capture before rug.ocr.");
        OcrEngineType type = string.Equals(engine, "paddle", StringComparison.OrdinalIgnoreCase)
            ? OcrEngineType.Paddle : OcrEngineType.WinRt;
        var blocks = await _ocr.RecognizeFrameAsync(_lastFrame, type, cancellationToken: ct).ConfigureAwait(false);
        return blocks;
    }

    private object? ToLuaValue(object? value)
    {
        if (value is CapturedFrame frame)
        {
            LuaTable table = NewTable();
            table["width"] = frame.Width;
            table["height"] = frame.Height;
            table["stride"] = frame.Stride;
            return table;
        }
        if (value is IReadOnlyList<OcrTextBlock> blocks)
        {
            LuaTable table = NewTable();
            for (int i = 0; i < blocks.Count; i++)
            {
                LuaTable item = NewTable();
                item["text"] = blocks[i].Text;
                item["score"] = blocks[i].Score;
                item["x"] = blocks[i].BoundingBox.X;
                item["y"] = blocks[i].BoundingBox.Y;
                item["width"] = blocks[i].BoundingBox.Width;
                item["height"] = blocks[i].BoundingBox.Height;
                table[i + 1] = item;
            }
            return table;
        }
        return value;
    }

    private LuaTable NewTable() => (LuaTable)_lua!.DoString("return {}")![0];

    private async Task<object?> ClickAsync(int x, int y, CancellationToken ct)
    {
        await _input.MoveMouseAsync(x, y, cancellationToken: ct).ConfigureAwait(false);
        await _input.ClickAsync(cancellationToken: ct).ConfigureAwait(false);
        return true;
    }

    private async Task<object?> PressKeyAsync(int key, CancellationToken ct)
    {
        await _input.KeyPressAsync(key, cancellationToken: ct).ConfigureAwait(false);
        return true;
    }

    public void Pause()
    {
        if (State is ScriptState.Stopped or ScriptState.Faulted or ScriptState.Uninitialized) return;
        _paused = true;
        ChangeState(ScriptState.Paused);
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        ChangeState(_pendingId is null ? ScriptState.Ready : ScriptState.Yielded);
    }

    public void Stop()
    {
        if (State == ScriptState.Stopped) return;
        _lifetime?.Cancel();
        ChangeState(ScriptState.Stopped);
    }

    private void ChangeState(ScriptState state)
    {
        ScriptState previous;
        lock (_stateGate)
        {
            if (_paused && state is ScriptState.Running or ScriptState.Yielded) state = ScriptState.Paused;
            previous = _state;
            if (previous == state || previous == ScriptState.Stopped && state != ScriptState.Stopped) return;
            _state = state;
        }
        StateChanged?.Invoke(this, new(previous, state));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Stop();
        await _driveGate.WaitAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _drive?.Dispose();
            _lua?.Dispose();
            _lifetime?.Dispose();
            _operations.Clear();
            _disposed = true;
        }
        finally { _gate.Release(); _driveGate.Release(); _gate.Dispose(); _driveGate.Dispose(); }
    }
}
