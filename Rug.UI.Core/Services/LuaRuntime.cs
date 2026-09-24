#nullable enable
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NLua;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Security;

namespace Rug.UI.Core.Services;

/// <summary>
/// Serializes access to one Lua state. Lua yields an operation descriptor; its .NET
/// task runs without holding the Lua gate and a later ResumeAsync supplies the result.
/// </summary>
public sealed class LuaRuntime : IScriptRuntime
{
    private const string Bridge = """
        local luaYield, luaCreate, luaResume, luaStatus = coroutine.yield, coroutine.create, coroutine.resume, coroutine.status
        local pack, luaType, raise = table.pack, type, error
        local getConfig, writeLog, shouldAbort = __rug_get_config, __rug_log, __rug_should_abort
        local sethook, traceback = debug.sethook, debug.traceback
        local thread, chunk
        rug = {}
        function rug.sleep(ms) return luaYield('__rug', 'sleep', ms) end
        function rug.capture() return luaYield('__rug', 'capture') end
        function rug.ocr(engine_type) return luaYield('__rug', 'ocr', engine_type) end
        function rug.click(x, y, button, mode) return luaYield('__rug', 'click', x, y, button, mode) end
        function rug.press_key(key, duration_ms) return luaYield('__rug', 'press_key', key, duration_ms) end
        rug.agent = {}
        function rug.agent.resolve_anomaly(reason, context)
          return luaYield('__rug', 'resolve_anomaly', reason, context, traceback('', 2))
        end
        function rug.get_config(key) return getConfig(key) end
        function rug.log(level, message)
          if message == nil then message, level = level, 'info' end
          return writeLog(level, message)
        end
        function __rug_drive(value)
          local r = pack(luaResume(thread, value))
          return luaStatus(thread), r
        end
        local function watchdog()
          if shouldAbort() then raise('__rug_watchdog_abort', 0) end
        end
        local function newThread(body)
          thread = luaCreate(body)
          sethook(thread, watchdog, '', 10000)
        end
        function __rug_set_chunk(value) chunk = value end
        function __rug_prepare_oneshot()
          newThread(function() chunk(); if luaType(on_tick) == 'function' then on_tick() end end)
        end
        function __rug_prepare_init()
          newThread(function() chunk(); if luaType(on_init) == 'function' then on_init() end end)
        end
        function __rug_begin_tick()
          newThread(function() if luaType(on_tick) == 'function' then on_tick() end end)
        end
        function __rug_begin_stop()
          newThread(function() if luaType(on_stop) == 'function' then on_stop() end end)
        end
        """;

    private readonly ICaptureService _capture;
    private readonly IOcrService _ocr;
    private readonly IInputService _input;
    private readonly ICoordinateMapper? _coordinateMapper;
    private readonly Func<nint, bool>? _ensureForeground;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<Guid, Task<object?>> _operations = new();
    private CancellationTokenSource? _lifetime;
    private PluginManifest? _manifest;
    private PermissionInterceptor? _permissions;
    private InputBridge? _inputBridge;
    private Lua? _lua;
    private LuaFunction? _drive;
    private LuaFunction? _prepareInit;
    private LuaFunction? _beginTick;
    private LuaFunction? _beginStop;
    private CapturedFrame? _lastFrame;
    private Guid? _pendingId;
    private Guid? _pendingAnomalyId;
    private string? _pendingAnomalyReason;
    private Func<string, string, Task<string>>? _anomalyHandler;
    private ScriptState _state = ScriptState.Uninitialized;
    private volatile bool _paused;
    private bool _disposed;
    private bool _lifecycleMode;
    private volatile bool _inStopHook;
    private long _stopHookDeadline;

    public LuaRuntime(ICaptureService capture, IOcrService ocr, IInputService input, ILogger<LuaRuntime> logger,
        ICoordinateMapper? coordinateMapper = null, Func<nint, bool>? ensureForeground = null)
    {
        _capture = capture;
        _ocr = ocr;
        _input = input;
        _logger = logger;
        _coordinateMapper = coordinateMapper;
        _ensureForeground = ensureForeground;
    }

    public ScriptState State { get { lock (_stateGate) return _state; } }
    public event EventHandler<ScriptStateChangedEventArgs>? StateChanged;

    public void ConfigureAnomalyHandler(Func<string, string, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_disposed || State is ScriptState.Running or ScriptState.Yielded or ScriptState.Paused or ScriptState.Stopped)
            throw new InvalidOperationException("Anomaly handler must be bound before script execution.");
        _anomalyHandler = handler;
    }

    public async Task InitializeAsync(string scriptPath, PluginManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(manifest.Permissions);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("Runtime is already initialized.");
        string source = await File.ReadAllTextAsync(scriptPath, ct).ConfigureAwait(false);
        _manifest = manifest;
        _permissions = new PermissionInterceptor(manifest, _logger);
        _inputBridge = new InputBridge(_input, _permissions, _coordinateMapper, _ensureForeground);
        if (manifest.Permissions.Contains(Permission.ControlInput))
        {
            await _inputBridge.BindAsync(manifest.TargetWindow,
                manifest.InputDelivery ?? (manifest.BackgroundInput
                    ? InputDeliveryMode.Win32PostMessage : InputDeliveryMode.Win32SendInput),
                ct).ConfigureAwait(false);
        }
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _lua = new Lua();
                _lua.State.Encoding = Encoding.UTF8;
                _lua.RegisterFunction("__rug_get_config", this, GetConfigMethod);
                _lua.RegisterFunction("__rug_log", this, LogMethod);
                _lua.RegisterFunction("__rug_should_abort", this, ShouldAbortMethod);
                _lua.DoString(Bridge);
                _lua["__rug_source"] = source;
                var chunk = (LuaFunction)_lua.DoString("return assert(load(__rug_source, '@plugin', 't', _ENV))")![0];
                ((LuaFunction)_lua["__rug_set_chunk"]!).Call(chunk);
                ((LuaFunction)_lua["__rug_prepare_oneshot"]!).Call();
                _drive = (LuaFunction)_lua["__rug_drive"]!;
                _prepareInit = (LuaFunction)_lua["__rug_prepare_init"]!;
                _beginTick = (LuaFunction)_lua["__rug_begin_tick"]!;
                _beginStop = (LuaFunction)_lua["__rug_begin_stop"]!;
                // Remove ambient file/process and CLR access from script globals.
                _lua.DoString("__rug_source=nil; __rug_set_chunk=nil; __rug_prepare_oneshot=nil; __rug_prepare_init=nil; __rug_begin_tick=nil; __rug_begin_stop=nil; __rug_drive=nil; __rug_should_abort=nil; __rug_get_config=nil; __rug_log=nil; coroutine=nil; io=nil; os=nil; package=nil; require=nil; dofile=nil; loadfile=nil; load=nil; debug=nil; luanet=nil; import=nil; load_assembly=nil; collectgarbage=nil");
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
    private static readonly System.Reflection.MethodInfo ShouldAbortMethod = typeof(LuaRuntime).GetMethod(nameof(ShouldAbort))!;

    public bool ShouldAbort() => _inStopHook
        ? Environment.TickCount64 >= Volatile.Read(ref _stopHookDeadline)
        : _lifetime?.IsCancellationRequested == true;

    public async Task PrepareLifecycleAsync()
    {
        await _driveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != ScriptState.Ready || _lifecycleMode) throw new InvalidOperationException("Lifecycle must be prepared before execution.");
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _prepareInit!.Call();
                _lifecycleMode = true;
            }
            finally { _gate.Release(); }
        }
        finally { _driveGate.Release(); }
    }

    public async Task BeginTickAsync()
    {
        await _driveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_lifecycleMode || State != ScriptState.Ready || _pendingId is not null)
                throw new InvalidOperationException("Previous lifecycle hook has not completed.");
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _beginTick!.Call(); }
            finally { _gate.Release(); }
        }
        finally { _driveGate.Release(); }
    }

    public async Task InvokeStopHookAsync(TimeSpan timeout)
    {
        if (!_lifecycleMode || _lua is null) return;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _driveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _inStopHook = true;
                Volatile.Write(ref _stopHookDeadline, Environment.TickCount64 + (long)timeout.TotalMilliseconds);
                await Task.Run(() =>
                {
                    _beginStop!.Call();
                    object[] response = _drive!.Call(new object?[] { null });
                    var values = (LuaTable)response[1];
                    if (values[1] is not true || !Equals(response[0], "dead"))
                        throw new InvalidOperationException("on_stop failed or yielded.");
                }).ConfigureAwait(false);
            }
            finally { _inStopHook = false; _gate.Release(); }
        }
        finally { _driveGate.Release(); }
    }

    public object? GetConfig(string key) => _manifest?.Configuration?.GetValueOrDefault(key);

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
                    if (_pendingAnomalyId == id)
                    {
                        _pendingAnomalyId = null;
                        string reason = _pendingAnomalyReason ?? "Unknown anomaly";
                        _pendingAnomalyReason = null;
                        ChangeState(ScriptState.Faulted);
                        return new(ScriptState.Faulted, Value: input,
                            Error: new InvalidOperationException($"Anomaly detected: {reason}. Record: {input}"));
                    }
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
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new InvalidOperationException(Convert.ToString(values[2], CultureInfo.InvariantCulture));
        }
        if (status == "dead")
        {
            ScriptState next = _lifecycleMode ? ScriptState.Ready : ScriptState.Stopped;
            ChangeState(next);
            return new(next, values[2]);
        }
        if (!Equals(values[2], "__rug"))
            throw new InvalidOperationException("Unexpected Lua yield; use rug.* asynchronous APIs.");
        string operation = Convert.ToString(values[3], CultureInfo.InvariantCulture)!;
        Task<object?> task = BeginOperation(operation, values, token);
        Guid id = Guid.NewGuid();
        _operations.Add(id, task);
        _pendingId = id;
        if (operation == "resolve_anomaly")
        {
            _pendingAnomalyId = id;
            _pendingAnomalyReason = Convert.ToString(values[4], CultureInfo.InvariantCulture);
        }
        ChangeState(ScriptState.Yielded);
        return new(State);
    }

    private Task<object?> BeginOperation(string operation, LuaTable args, CancellationToken ct)
    {
        string permission = operation switch
        {
            "sleep" => Permission.Timer, "capture" => Permission.VisionCapture, "ocr" => Permission.VisionOcr,
            "click" or "press_key" => Permission.ControlInput,
            "resolve_anomaly" => Permission.Agent,
            _ => throw new InvalidOperationException($"Unknown rug operation: {operation}")
        };
        _permissions!.Demand(permission, operation == "resolve_anomaly" ? "rug.agent.resolve_anomaly" : $"rug.{operation}");
        return operation switch
        {
            "sleep" => SleepAsync(Int(args[4]), ct),
            "capture" => CaptureAsync(ct),
            "ocr" => OcrAsync(Convert.ToString(args[4], CultureInfo.InvariantCulture), ct),
            "click" => ClickAsync(Int(args[4]), Int(args[5]), ParseButton(args[6]), ParseMode(args[7]), ct),
            "press_key" => PressKeyAsync(Int(args[4]), args[5] is null ? 0 : Int(args[5]), ct),
            "resolve_anomaly" => ResolveAnomalyAsync(args),
            _ => throw new InvalidOperationException()
        };
    }

    private static int Int(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static MouseButton ParseButton(object? value)
    {
        if (value is null) return MouseButton.Left;
        if (value is string name)
            return name.ToLowerInvariant() switch
            {
                "left" => MouseButton.Left, "right" => MouseButton.Right,
                "middle" => MouseButton.Middle, "x1" => MouseButton.XButton1,
                "x2" => MouseButton.XButton2,
                _ => throw new ArgumentException($"Unknown mouse button: {name}")
            };
        int numeric = Int(value);
        if (!Enum.IsDefined((MouseButton)numeric)) throw new ArgumentOutOfRangeException(nameof(value));
        return (MouseButton)numeric;
    }

    private static InputDeliveryMode? ParseMode(object? value)
    {
        if (value is null) return null;
        if (value is not string name) throw new ArgumentException("Input mode must be a string.");
        return name.ToLowerInvariant() switch
        {
            "win32sendinput" or "sendinput" or "foreground" => InputDeliveryMode.Win32SendInput,
            "win32postmessage" or "postmessage" or "background" => InputDeliveryMode.Win32PostMessage,
            _ => throw new ArgumentException($"Unknown input delivery mode: {name}")
        };
    }

    private async Task<object?> ResolveAnomalyAsync(LuaTable args)
    {
        Func<string, string, Task<string>> handler = _anomalyHandler
            ?? throw new InvalidOperationException("Anomaly handler is not configured.");
        string reason = Convert.ToString(args[4], CultureInfo.InvariantCulture) ?? "Unknown anomaly";
        string detail = Convert.ToString(args[5], CultureInfo.InvariantCulture) ?? "";
        string traceback = Convert.ToString(args[6], CultureInfo.InvariantCulture) ?? "";
        return await handler(reason, detail + Environment.NewLine + traceback).ConfigureAwait(false);
    }

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

    private async Task<object?> ClickAsync(int x, int y, MouseButton button, InputDeliveryMode? mode, CancellationToken ct)
    {
        await _inputBridge!.ClickAsync(x, y, button, mode, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<object?> PressKeyAsync(int key, int durationMs, CancellationToken ct)
    {
        await _inputBridge!.PressKeyAsync(key, durationMs, ct).ConfigureAwait(false);
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
            _prepareInit?.Dispose();
            _beginTick?.Dispose();
            _beginStop?.Dispose();
            _lua?.Dispose();
            _lifetime?.Dispose();
            _operations.Clear();
            if (_inputBridge is not null) await _inputBridge.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { _gate.Release(); _driveGate.Release(); _gate.Dispose(); _driveGate.Dispose(); }
    }
}
