#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
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
        local getConfig, getState, setState, writeLog, shouldAbort, clock =
          __rug_get_config, __rug_get_state, __rug_set_state, __rug_log, __rug_should_abort, __rug_ticks
        local protectedCall = pcall
        local function hostCall(callback, ...)
          local ok, result, failure = protectedCall(callback, ...)
          if not ok or failure ~= nil or luaType(result) == 'userdata' then raise('Host API rejected the call', 2) end
          return result
        end
        local min = math.min
        local sethook, traceback = debug.sethook, debug.traceback
        local thread, chunk
        rug = {}
        function rug.sleep(ms) return luaYield('__rug', 'sleep', ms) end
        function rug.capture() return luaYield('__rug', 'capture') end
        function rug.ocr(region, language, engine) return luaYield('__rug', 'ocr', region, language, engine) end
        function rug.find_image(path, threshold, region)
          return luaYield('__rug', 'find_image', path, threshold, region)
        end
        local capture, findImage, sleep = rug.capture, rug.find_image, rug.sleep
        function rug.wait_image(path, timeout_ms, threshold, interval_ms)
          local timeout, interval = timeout_ms or 5000, interval_ms or 100
          if luaType(timeout) ~= 'number' or timeout < 0 or timeout > 600000 or
             luaType(interval) ~= 'number' or interval <= 0 or interval > 60000 then
            raise('wait_image timeout/interval is invalid', 2)
          end
          local deadline = clock() + timeout
          while true do
            if capture() ~= nil then
              local hit = findImage(path, threshold)
              if hit ~= nil then return hit end
            end
            local remaining = deadline - clock()
            if remaining <= 0 then return nil end
            sleep(min(interval, remaining))
          end
        end
        function rug.click(x, y, button, mode) return luaYield('__rug', 'click', x, y, button, mode) end
        function rug.press_key(key, duration_ms) return luaYield('__rug', 'press_key', key, duration_ms) end
        rug.agent = {}
        function rug.agent.resolve_anomaly(reason, context)
          return luaYield('__rug', 'resolve_anomaly', reason, context, traceback('', 2))
        end
        function rug.get_config(key) return hostCall(getConfig, key) end
        function rug.get_state(key) return hostCall(getState, key) end
        function rug.set_state(key, value) return hostCall(setState, key, value) end
        function rug.log(level, message)
          if message == nil then message, level = level, 'info' end
          return hostCall(writeLog, level, message)
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
    private readonly ITemplateMatchService _matcher;
    private readonly IInputService _input;
    private readonly ICoordinateMapper? _coordinateMapper;
    private readonly Func<nint, bool>? _ensureForeground;
    private readonly ILogger _logger;
    private readonly ICommandBus _commandBus;
    private readonly bool _ownsCommandBus;
    private RunIdentity? _identity;
    private AutomationGateway? _gateway;
    private readonly IPluginAuthorization _authorization;
    private AuthorizationLease? _authorizationLease;
    private AutomationCallContext? _caller;
    private StateScope _instanceScope = StateScope.Instance("standalone", Guid.NewGuid());
    private IReadOnlyDictionary<string, object?>? _configSnapshot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _driveGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<Guid, Task<object?>> _operations = new();
    private CancellationTokenSource? _lifetime;
    private PluginManifest? _manifest;
    private PermissionInterceptor? _permissions;
    private InputBridge? _inputBridge;
    private TaskExecutionContext? _context;
    private IInputArbiter _inputArbiter = InputArbiter.Shared;
    private VisualEngineBridge? _visual;
    private Lua? _lua;
    private LuaFunction? _drive;
    private LuaFunction? _prepareInit;
    private LuaFunction? _beginTick;
    private LuaFunction? _beginStop;
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
        ICoordinateMapper? coordinateMapper = null, Func<nint, bool>? ensureForeground = null,
        ITemplateMatchService? templateMatcher = null, ICommandBus? commandBus = null, IPluginAuthorization? authorization = null)
    {
        _authorization = authorization ?? new PluginAuthorization();
        _capture = capture;
        _ocr = ocr;
        _matcher = templateMatcher ?? new TemplateMatchService();
        _input = input;
        _logger = logger;
        _commandBus = commandBus ?? new CommandBus(new StateStore());
        _ownsCommandBus = commandBus is null;
        _commandBus.OpenInstance(_instanceScope);
        _coordinateMapper = coordinateMapper;
        _ensureForeground = ensureForeground;
    }

    public ScriptState State { get { lock (_stateGate) return _state; } }
    public event EventHandler<ScriptStateChangedEventArgs>? StateChanged;
    public IPluginAuthorization Authorization => _gateway?.Authorization ?? _authorization;
    public RunIdentity? BoundIdentity => _identity;
    public AutomationGateway Gateway => _gateway ?? throw new InvalidOperationException("Runtime is not initialized.");
    public void ConfigureInputArbiter(IInputArbiter arbiter)
    {
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("Input arbiter must be bound before initialization.");
        _inputArbiter = arbiter;
    }
    public void ConfigureGateway(AutomationGateway gateway)
    {
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("Gateway must be bound before initialization.");
        _gateway = gateway;
    }
    public void ConfigureLifecycle(TaskExecutionContext context)
    {
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("Lifecycle must be bound before initialization.");
        _context = context;
    }

    public void ConfigureState(RunIdentity identity, IReadOnlyDictionary<string, object?> launchInputs,
        IReadOnlyDictionary<string, object?> pluginConfig)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(launchInputs);
        ArgumentNullException.ThrowIfNull(pluginConfig);
        if (State != ScriptState.Uninitialized) throw new InvalidOperationException("State identity must be bound before initialization.");
        _commandBus.CloseInstance(_instanceScope);
        _instanceScope = identity.InstanceScope;
        _identity = identity;
        _commandBus.OpenInstance(_instanceScope);
        var copy = new StateStore();
        StateScope configurationScope = StateScope.Plugin(identity.PluginId);
        foreach (var pair in pluginConfig) copy.SetState(configurationScope, pair.Key, pair.Value);
        _configSnapshot = copy.Snapshot(configurationScope);
        foreach (var pair in launchInputs) _commandBus.SetState(_instanceScope, pair.Key, pair.Value);
    }

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
        _identity ??= new RunIdentity(manifest.Id, _instanceScope.InstanceId, 1,
            manifest.TargetWindow == 0 ? null : Guid.NewGuid(), manifest.TargetWindow);
        PluginManifest gatewayManifest = manifest with { Id = _identity.PluginId };
        _gateway ??= new AutomationGateway(_logger, getPlugin: (plugin, _) =>
            Task.FromResult(plugin == gatewayManifest.Id ? gatewayManifest : throw new KeyNotFoundException()),
            authorization: _authorization);
        _caller = _gateway.BindContext(gatewayManifest, "lua:" + _identity.InstanceId, AutomationSource.Lua, _identity);
        _authorizationLease = _gateway.Authorization.Admit(_caller.Authorization, manifest.Permissions);
        _lifetime = manifest.Permissions.Contains(Permission.ControlInput)
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _authorizationLease.Token, _inputArbiter.AutomationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, _authorizationLease.Token);
        _permissions = new PermissionInterceptor(manifest, _logger, _gateway.Authorization, _caller.Authorization);
        _visual = new VisualEngineBridge(_capture, _ocr, _matcher, _permissions, manifest.PluginDirectory);
        _inputBridge = new InputBridge(_input, _permissions, _coordinateMapper, _ensureForeground,
            _inputArbiter, InputOwner.ForRun(_identity), () =>
            {
                // Recheck after bridge/arbiter queues and between composite steps.
                // Mandatory releases bypass this business check.
                _context?.ValidateBusiness();
                _lifetime.Token.ThrowIfCancellationRequested();
            });
        if (manifest.Permissions.Contains(Permission.ControlInput))
            await _inputBridge.BindAsync(manifest.TargetWindow,
                manifest.InputDelivery ?? (manifest.BackgroundInput ? InputDeliveryMode.Win32PostMessage : InputDeliveryMode.Win32SendInput),
                _lifetime.Token).ConfigureAwait(false);
        _gateway.RegisterRun(_identity, gatewayManifest, _lifetime.Token, ExecuteAutomationAsync, _context);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                _lua = new Lua();
                _lua.State.Encoding = Encoding.UTF8;
                _lua.RegisterFunction("__rug_get_config", this, GetConfigMethod);
                _lua.RegisterFunction("__rug_get_state", this, GetStateMethod);
                _lua.RegisterFunction("__rug_set_state", this, SetStateMethod);
                _lua.RegisterFunction("__rug_log", this, LogMethod);
                _lua.RegisterFunction("__rug_should_abort", this, ShouldAbortMethod);
                _lua.RegisterFunction("__rug_ticks", this, TicksMethod);
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
                _lua.DoString("__rug_source=nil; __rug_set_chunk=nil; __rug_prepare_oneshot=nil; __rug_prepare_init=nil; __rug_begin_tick=nil; __rug_begin_stop=nil; __rug_drive=nil; __rug_should_abort=nil; __rug_get_config=nil; __rug_get_state=nil; __rug_set_state=nil; __rug_log=nil; __rug_ticks=nil; coroutine=nil; io=nil; os=nil; package=nil; require=nil; dofile=nil; loadfile=nil; load=nil; debug=nil; luanet=nil; import=nil; load_assembly=nil; collectgarbage=nil");
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

    private static readonly System.Reflection.MethodInfo GetConfigMethod = typeof(LuaRuntime).GetMethod(nameof(GetLuaConfig), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo GetStateMethod = typeof(LuaRuntime).GetMethod(nameof(GetLuaState), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo SetStateMethod = typeof(LuaRuntime).GetMethod(nameof(SetLuaState), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo LogMethod = typeof(LuaRuntime).GetMethod(nameof(LogLua), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    private static readonly System.Reflection.MethodInfo ShouldAbortMethod = typeof(LuaRuntime).GetMethod(nameof(ShouldAbort))!;
    private static readonly System.Reflection.MethodInfo TicksMethod = typeof(LuaRuntime).GetMethod(nameof(Ticks))!;

    public long Ticks() => Environment.TickCount64;

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

    public object? GetConfig(string key) => StateStore.CloneValue(
        (_configSnapshot ?? _manifest?.Configuration)?.GetValueOrDefault(key));

    public object? GetState(string key)
    {
        object? value = _commandBus.GetState<object?>(_instanceScope, key);
        if (value is not JsonElement json) return value;
        // NLua treats boxed JsonElement scalars as userdata, including a truthy false.
        // Keep containers structured so typed host bindings can still read them.
        return json.ValueKind switch
        {
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Number when json.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => json
        };
    }

    // Never hand CLR userdata to untrusted Lua through state/config. Host snapshots retain their original JSON shape.
    private static string HostString(object? value) => value as string ?? throw new ArgumentException("Expected a string.");
    private object? GetLuaState(object? key, out string? failure) =>
        HostValue(() => ToLuaJson(JsonSerializer.SerializeToElement(GetState(HostString(key)))), out failure);
    private object? GetLuaConfig(object? key, out string? failure) =>
        HostValue(() => ToLuaJson(JsonSerializer.SerializeToElement(GetConfig(HostString(key)))), out failure);
    private object? SetLuaState(object? key, object? value, out string? failure) =>
        HostValue(() => { SetState(HostString(key), value); return null; }, out failure);
    private object? LogLua(object? level, object? message, out string? failure) =>
        HostValue(() => { Log(HostString(level), HostString(message)); return null; }, out failure);
    private static object? HostValue(Func<object?> read, out string? failure)
    {
        try { failure = null; return read(); }
        catch (Exception) { failure = "Host API rejected the call"; return null; }
    }
    private object? ToLuaJson(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                LuaTable objectTable = NewTable();
                foreach (var property in value.EnumerateObject()) objectTable[property.Name] = ToLuaJson(property.Value);
                return objectTable;
            case JsonValueKind.Array:
                LuaTable arrayTable = NewTable();
                int index = 1;
                foreach (var item in value.EnumerateArray()) arrayTable[index++] = ToLuaJson(item);
                return arrayTable;
            case JsonValueKind.String: return value.GetString();
            case JsonValueKind.Number: return value.TryGetInt64(out long integer) ? (object)integer : value.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }

    public void SetState(string key, object? value)
    {
        if (value is not (null or bool or string or double or long or int))
            throw new ArgumentException("Lua state values must be nil, Boolean, string or number.", nameof(value));
        _commandBus.SetState(_instanceScope, key, value);
    }

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
        if (_paused) return new(_context?.IsBusinessClosed == true ? ScriptState.Stopped : ScriptState.Paused);
        if (resume != (_pendingId is not null))
            throw new InvalidOperationException(resume ? "No yielded operation to resume." : "Use ResumeAsync for a yielded operation.");
        if (!resume && State != ScriptState.Ready) throw new InvalidOperationException("Runtime is not ready.");
        CancellationToken token = _lifetime?.Token ?? throw new InvalidOperationException("Runtime is not initialized.");
        object? input = null;
        Guid? completedId = null;
        if (resume)
        {
            Guid id = _pendingId!.Value;
            try
            {
                Task<object?> pending = _operations[id];
                if (_context is not null)
                {
                    await Task.WhenAny(pending, _context.PauseSignal).WaitAsync(token).ConfigureAwait(false);
                    if (_context.PauseRequested) return new(ScriptState.Paused);
                }
                input = await pending.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested || error is HostStopAdmissionCancellation)
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
                using var segment = _context?.TryEnterLuaSegment();
                if (_context is not null && segment is null)
                    return new(token.IsCancellationRequested || _context.IsBusinessClosed ? ScriptState.Stopped : ScriptState.Paused);
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
        catch (OperationCanceledException error) when (token.IsCancellationRequested || error is HostStopAdmissionCancellation)
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
        // A paused run can retain a failed/completed pending task until Resume or Stop.
        // Observe exceptions even when Stop disposes the coroutine without consuming it.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);
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

    /// <summary>@shape args: packed coroutine result, operation arguments start at index 4; result: gateway task.
    /// @source args: LuaRuntime.ResumeLua</summary>
    private Task<object?> BeginOperation(string operation, LuaTable args, CancellationToken ct)
    {
        if (operation == "resolve_anomaly")
        {
            _permissions!.Demand(Permission.Agent, "rug.agent.resolve_anomaly");
            return ResolveAnomalyAsync(args);
        }
        AutomationOperation type = operation switch
        {
            "sleep" => AutomationOperation.Sleep, "capture" => AutomationOperation.Capture,
            "ocr" => AutomationOperation.Ocr, "find_image" => AutomationOperation.FindImage,
            "click" => AutomationOperation.Click, "press_key" => AutomationOperation.PressKey,
            _ => (AutomationOperation)(-1)
        };
        JsonElement parameters;
        try { parameters = ParseOperationParameters(operation, args); }
        catch (Exception error) when (error is ArgumentException or JsonException or FormatException or InvalidCastException or OverflowException)
        {
            // Invalid Lua arguments still enter Gateway and produce an audited InvalidParameters result.
            parameters = JsonSerializer.SerializeToElement<object?>(null);
        }
        return ExecuteGatewayAsync(type, parameters, ct);
    }

    private static JsonElement ParseOperationParameters(string operation, LuaTable args)
    {
        // Copy Lua arguments while holding the Lua gate; never retain a LuaTable in a request.
        object parameters = operation switch
        {
            "sleep" => new { ms = Int(args[4]) },
            "capture" => new { },
            "ocr" => ParseOcrParameters(args[4], args[5], args[6]),
            "find_image" => new { path = args[4] as string ?? throw new ArgumentException("Template path must be a string."),
                threshold = args[5] is null ? 0.8f : Number(args[5]), region = ParseRegion(args[6]) },
            "click" => new { x = Int(args[4]), y = Int(args[5]), button = ParseButton(args[6]), mode = ParseMode(args[7]) },
            "press_key" => new { key = Int(args[4]), durationMs = args[5] is null ? 0 : Int(args[5]) },
            _ => throw new ArgumentException($"Unknown rug operation: {operation}")
        };
        return JsonSerializer.SerializeToElement(parameters);
    }

    private Task<object?> ExecuteGatewayAsync(AutomationOperation operation, JsonElement parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _context?.Cancellation.Token.ThrowIfCancellationRequested();
        if (_context?.PauseRequested == true) return AfterActivationAsync();
        var request = new OperationRequest(operation, parameters, Guid.NewGuid(), _identity!.InstanceId.ToString("N"),
            OperationTarget.ForRun(_identity), DateTimeOffset.UtcNow.AddMinutes(10));
        Task<OperationResult> pending = _gateway!.ExecuteAsync(_caller!, request, ct);
        if (pending.IsCompletedSuccessfully)
        {
            if (Deferred(pending.Result)) return AfterActivationAsync();
            ct.ThrowIfCancellationRequested();
            _context?.Cancellation.Token.ThrowIfCancellationRequested();
            return Task.FromResult(MapGatewayResult(pending.Result, operation));
        }
        return AwaitResultAsync();
        bool Deferred(OperationResult result) => result.Error == OperationError.InvalidLifecycleState &&
            !result.OutcomeUncertain && _context?.PauseRequested == true;
        async Task<object?> AwaitResultAsync()
        {
            OperationResult result = await pending.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _context?.Cancellation.Token.ThrowIfCancellationRequested();
            return Deferred(result) ? await AfterActivationAsync().ConfigureAwait(false) : MapGatewayResult(result, operation);
        }
        // Pause won admission before any side effect. Resume admits this unexecuted
        // action with a fresh ID; the rejected ID remains protected by deduplication.
        async Task<object?> AfterActivationAsync()
        {
            try { await _context!.WaitForBusinessAsync(ct).ConfigureAwait(false); }
            catch (AutomationGateway.GatewayRejectedException error) when
                (error.Error == OperationError.InvalidLifecycleState && _context!.IsBusinessClosed)
            { throw new HostStopAdmissionCancellation(); }
            return await ExecuteGatewayAsync(operation, parameters, ct).ConfigureAwait(false);
        }
    }
    // Only a lifecycle admission refusal for this closed host run gets this tag.
    // Do not classify arbitrary Lua/service/permission errors by a stopping flag.
    private sealed class HostStopAdmissionCancellation : OperationCanceledException;
    private object? MapGatewayResult(OperationResult result, AutomationOperation operation)
    {
        if (result.Status == OperationStatus.Rejected && result.Error == OperationError.InvalidLifecycleState && _context?.IsBusinessClosed == true)
            throw new HostStopAdmissionCancellation();
        if (result.Error == OperationError.PermissionDenied)
            throw new Rug.UI.Core.Exceptions.PermissionDeniedException(_identity!.PluginId,
                result.DeniedPermission ?? ManifestAutomationPermissionChecker.RequiredPermission(operation)!, "rug." + (operation switch
                {
                    AutomationOperation.FindImage => "find_image", AutomationOperation.PressKey => "press_key",
                    _ => operation.ToString().ToLowerInvariant()
                }));
        return result.RequireSuccess();
    }

    private Task<object?> ExecuteAutomationAsync(AutomationOperation operation, JsonElement parameters, CancellationToken ct) =>
        operation switch
        {
            AutomationOperation.Sleep => SleepAsync(parameters.GetProperty("ms").GetInt32(), ct),
            AutomationOperation.Capture => CaptureAsync(ct),
            AutomationOperation.FindImage => FindImageAsync(parameters, ct),
            AutomationOperation.Ocr => OcrAsync(parameters, ct),
            AutomationOperation.Click => ClickAsync(parameters.GetProperty("x").GetInt32(), parameters.GetProperty("y").GetInt32(),
                ReadOptional<MouseButton>(parameters, "button") ?? MouseButton.Left, ReadOptional<InputDeliveryMode>(parameters, "mode"), ct),
            AutomationOperation.PressKey => PressKeyAsync(parameters.GetProperty("key").GetInt32(),
                ReadOptional<int>(parameters, "durationMs") ?? 0, ct),
            _ => throw new ArgumentException("Unsupported runtime operation.")
        };
    private static T? ReadOptional<T>(JsonElement parameters, string key) where T : struct =>
        parameters.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.Deserialize<T>() : null;

    private static int Int(object? value) => value switch
    {
        int integer => integer,
        long integer when integer is >= int.MinValue and <= int.MaxValue => (int)integer,
        double number when double.IsFinite(number) && number >= int.MinValue && number <= int.MaxValue && Math.Truncate(number) == number => (int)number,
        _ => throw new ArgumentException("Argument must be an Int32 number.")
    };
    private static float Number(object? value) => value switch
    {
        long number => number,
        double number => (float)number,
        _ => throw new ArgumentException("Argument must be a number.")
    };

    private static Rect? ParseRegion(object? value)
    {
        if (value is null) return null;
        if (value is not LuaTable region) throw new ArgumentException("Region must be a table with x, y, width and height.");
        return new Rect(Int(region["x"]), Int(region["y"]),
            Int(region["width"]), Int(region["height"]));
    }

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
        IDisposable? work;
        try { work = _context?.EnterOperation(input: false); }
        catch (AutomationGateway.GatewayRejectedException error) when
            (error.Error == OperationError.InvalidLifecycleState && _context!.IsBusinessClosed)
        { throw new HostStopAdmissionCancellation(); }
        // Convert only direct admission, never permission or archive failures.
        using var admittedWork = work;
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
        CapturedFrame? frame = await _visual!.CaptureAsync(ct).ConfigureAwait(false);
        // Cache descriptors for deduplication; the visual bridge owns the current pixel buffer.
        return frame is null ? null : new CapturedFrameMetadata(frame.Width, frame.Height, frame.Stride);
    }

    private async Task<object?> FindImageAsync(JsonElement parameters, CancellationToken ct)
    {
        string template = parameters.GetProperty("path").GetString() ?? throw new ArgumentException("Template path must be a string.");
        return await _visual!.FindImageAsync(template, ReadOptional<float>(parameters, "threshold") ?? 0.8f,
            ReadOptional<Rect>(parameters, "region"), ct).ConfigureAwait(false);
    }

    private static object ParseOcrParameters(object? first, object? second, object? third)
    {
        Rect? region = ParseRegion(first is LuaTable ? first : null);
        OcrEngineType engine = OcrEngineType.WinRt;
        if (second is not null and not string) throw new ArgumentException("OCR language must be a string.");
        string? language = second as string;
        if (first is string selector)
        {
            if (selector.Equals("paddle", StringComparison.OrdinalIgnoreCase)) engine = OcrEngineType.Paddle;
            else if (!selector.Equals("winrt", StringComparison.OrdinalIgnoreCase)) language = selector;
        }
        else if (first is not null && first is not LuaTable)
            throw new ArgumentException("OCR region must be a table or an engine name.");
        if (third is string selectedEngine)
        {
            if (selectedEngine.Equals("paddle", StringComparison.OrdinalIgnoreCase))
                engine = OcrEngineType.Paddle;
            else if (selectedEngine.StartsWith("paddle:", StringComparison.OrdinalIgnoreCase) &&
                     selectedEngine.Length > "paddle:".Length)
            {
                engine = OcrEngineType.Paddle;
                language = selectedEngine["paddle:".Length..];
            }
            else if (!selectedEngine.Equals("winrt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown OCR engine: {selectedEngine}");
        }
        else if (third is not null)
            throw new ArgumentException("OCR engine must be a string.");
        return new { region, language, engine };
    }
    private async Task<object?> OcrAsync(JsonElement parameters, CancellationToken ct)
    {
        string? language = parameters.TryGetProperty("language", out var value) ? value.GetString() : null;
        OcrEngineType engine = ReadOptional<OcrEngineType>(parameters, "engine") ?? OcrEngineType.WinRt;
        if (!Enum.IsDefined(engine)) throw new ArgumentException("Unknown OCR engine.");
        return await _visual!.OcrAsync(ReadOptional<Rect>(parameters, "region"), language, engine, ct).ConfigureAwait(false);
    }

    private object? ToLuaValue(object? value)
    {
        if (value is CapturedFrameMetadata frame)
        {
            LuaTable table = NewTable();
            table["width"] = frame.Width;
            table["height"] = frame.Height;
            table["stride"] = frame.Stride;
            return table;
        }
        if (value is ImageFindResult hit)
        {
            LuaTable table = NewTable();
            table["x"] = hit.X;
            table["y"] = hit.Y;
            table["similarity"] = hit.Similarity;
            return table;
        }
        if (value is VisionOcrResult recognized)
        {
            LuaTable table = NewTable();
            LuaTable blockTable = NewTable();
            table["text"] = recognized.Text;
            for (int i = 0; i < recognized.Blocks.Count; i++)
            {
                OcrTextBlock block = recognized.Blocks[i];
                LuaTable item = NewTable();
                item["text"] = block.Text;
                item["score"] = block.Score;
                item["x"] = block.BoundingBox.X;
                item["y"] = block.BoundingBox.Y;
                item["width"] = block.BoundingBox.Width;
                item["height"] = block.BoundingBox.Height;
                table[i + 1] = item;
                blockTable[i + 1] = item;
            }
            table["blocks"] = blockTable;
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
        if (!_paused || _disposed || State is ScriptState.Stopped or ScriptState.Faulted) return;
        _paused = false;
        ChangeState(_pendingId is null ? ScriptState.Ready : ScriptState.Yielded);
    }

    public void Stop()
    {
        // A scheduler-owned run remains addressable for Stop until host cleanup
        // completes. Business admission is already closed by its lifecycle/token.
        if (_context is null && _identity is not null) _gateway?.EndRun(_identity.InstanceId);
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
        if (state is ScriptState.Stopped or ScriptState.Faulted)
        {
            if (_context is null && _identity is not null) _gateway?.EndRun(_identity.InstanceId);
            _lifetime?.Cancel();
        }
        StateChanged?.Invoke(this, new(previous, state));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Stop();
        await _driveGate.WaitAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        try
        {
            Attempt(() => _drive?.Dispose());
            Attempt(() => _prepareInit?.Dispose());
            Attempt(() => _beginTick?.Dispose());
            Attempt(() => _beginStop?.Dispose());
            Attempt(() => _lua?.Dispose());
            Attempt(() => _authorizationLease?.Dispose());
            _operations.Clear();
            Attempt(() => _visual?.Dispose());
            try { if (_inputBridge is not null) await _inputBridge.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            Attempt(() => _lifetime?.Dispose());
            Attempt(() => _commandBus.CloseInstance(_instanceScope));
            if (_ownsCommandBus) Attempt(_commandBus.Dispose);
            _disposed = true;
        }
        finally { _gate.Release(); _driveGate.Release(); _gate.Dispose(); _driveGate.Dispose(); }
        if (errors.Count > 0) throw new AggregateException("Lua runtime cleanup failed.", errors);
        void Attempt(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
    }
}
