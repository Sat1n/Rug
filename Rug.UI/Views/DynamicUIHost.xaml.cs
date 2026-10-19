using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;
using Rug.UI.Services;

namespace Rug.UI.Views;

public sealed partial class DynamicUIHost : Page
{
    private readonly SchemaParser _parser = new();
    private UIFactory? _factory;
    private IHotReloadService? _hotReload;
    private IDisposable? _hotReloadRegistration;
    private string? _pluginId;
    private ICommandBus? _bus;
    private RunIdentity? _activeIdentity;
    private IReadOnlyDictionary<string, IReadOnlyList<SchemaStep>> _displayFallbacks =
        new Dictionary<string, IReadOnlyList<SchemaStep>>();
    private CancellationTokenSource? _noticeTimer;
    private long _viewRevision;

    public DynamicUIHost()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            DetachHotReload();
            Clear();
        };
    }

    public void LoadJson(string json, ICommandBus? bus = null, string? pluginId = null)
    {
        _pluginId = pluginId ?? _pluginId;
        UISchemaNode schema = _parser.Parse(json);
        Render(schema, bus ?? App.GetService<ICommandBus>());
    }

    public async Task LoadFileAsync(string path, ICommandBus? bus = null,
        CancellationToken cancellationToken = default, string? pluginId = null)
    {
        UISchemaNode schema = await _parser.ParseFileAsync(path, cancellationToken);
        _pluginId = pluginId ?? _pluginId ??
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))) ?? "preview";
        Render(schema, bus ?? App.GetService<ICommandBus>());
    }

    /// <summary>Bind this plugin's current run; null clears its live InstanceState binding.</summary>
    public void SetActiveInstance(RunIdentity? identity,
        IReadOnlyDictionary<string, IReadOnlyList<SchemaStep>>? displayFallbacks = null)
    {
        if (identity is not null && _pluginId is not null &&
            !string.Equals(identity.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Run identity belongs to a different plugin.", nameof(identity));
        var detached = displayFallbacks?.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<SchemaStep>)pair.Value.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, IReadOnlyList<SchemaStep>>();
        RunOnUi(() =>
        {
            _activeIdentity = identity;
            _displayFallbacks = detached;
            _factory?.SetActiveInstance(identity?.InstanceId, detached);
        });
    }

    public void ShowLoadError(Exception error) =>
        ShowNotice(InfoBarSeverity.Error, "UI Schema 加载失败", error.ToString());

    public void AttachHotReload(IHotReloadService service, string pluginId, ICommandBus? bus = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        DetachHotReload();
        _hotReload = service;
        _pluginId = pluginId;
        _bus = bus ?? App.GetService<ICommandBus>();
        _hotReloadRegistration = service.RegisterUiHandler(pluginId, PrepareUiAsync);
        service.Reloaded += OnReloaded;
        service.ReloadFailed += OnReloadFailed;
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }))
            throw new InvalidOperationException("UI dispatcher rejected the reload request.");
        return completion.Task;
    }
    private Task<IPluginUiUpdate> PrepareUiAsync(PluginSnapshot snapshot, CancellationToken token) => OnUiAsync<IPluginUiUpdate>(() =>
    {
        token.ThrowIfCancellationRequested();
        var factory = new UIFactory(_bus!, snapshot.PluginId);
        try
        {
            var tree = factory.Build(_parser.Parse(snapshot.UiJson ?? "{\"type\":\"Page\",\"id\":\"empty-plugin-ui\"}"));
            factory.SetActiveInstance(_activeIdentity?.InstanceId, _displayFallbacks);
            return new UiUpdate(this, factory, tree, _viewRevision);
        }
        catch { factory.Dispose(); throw; }
    });
    private sealed class UiUpdate(DynamicUIHost owner, UIFactory next, UIElement tree, long revision) : IPluginUiUpdate
    {
        private UIFactory? _old;
        private UIElement[] _oldTrees = [];
        private bool _committed, _rolledBack;
        public async Task CommitAsync(CancellationToken token) => await owner.OnUiAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (owner._viewRevision != revision || owner._hotReload is null) throw new OperationCanceledException("UI host was replaced.");
            next.SetActiveInstance(owner._activeIdentity?.InstanceId, owner._displayFallbacks);
            _old = owner._factory; _oldTrees = owner.DynamicContent.Children.ToArray();
            try { owner.DynamicContent.Children.Clear(); owner.DynamicContent.Children.Add(tree); }
            catch { owner.DynamicContent.Children.Clear(); foreach (var old in _oldTrees) owner.DynamicContent.Children.Add(old); throw; }
            next.CommandFailed += owner.OnCommandFailed; owner._factory = next; owner._viewRevision++; _committed = true; return true;
        });
        public async Task RollbackAsync() => await owner.OnUiAsync(() =>
        {
            if (_committed && ReferenceEquals(owner._factory, next))
            {
                owner.DynamicContent.Children.Clear(); foreach (var old in _oldTrees) owner.DynamicContent.Children.Add(old);
                owner._factory = _old; owner._viewRevision++; _rolledBack = true;
            }
            else if (_committed) { _old?.Dispose(); _old = null; _rolledBack = true; }
            return true;
        });
        public async ValueTask DisposeAsync() => await owner.OnUiAsync(() =>
        {
            if (_committed && !_rolledBack) { if (_old is not null) { _old.CommandFailed -= owner.OnCommandFailed; _old.Dispose(); } next.CommitDefaults(); }
            else { next.CommandFailed -= owner.OnCommandFailed; next.Dispose(); }
            return true;
        });
    }
    private void Render(UISchemaNode schema, ICommandBus bus)
    {
        // Build the replacement before removing the old view. Invalid schemas keep the last good UI.
        var nextFactory = new UIFactory(bus, _pluginId ?? "preview");
        UIElement tree;
        try { tree = nextFactory.Build(schema); nextFactory.SetActiveInstance(_activeIdentity?.InstanceId, _displayFallbacks); nextFactory.CommitDefaults(); }
        catch
        {
            nextFactory.Dispose();
            throw;
        }
        Clear();
        _factory = nextFactory;
        _factory.SetActiveInstance(_activeIdentity?.InstanceId, _displayFallbacks);
        _factory.CommandFailed += OnCommandFailed;
        DynamicContent.Children.Add(tree);
        ErrorBanner.IsOpen = false;
    }

    private void OnReloaded(object? sender, HotReloadEventArgs e)
    {
        if (!string.Equals(e.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;
        if (e.Status == PluginUpdateStatus.Superseded) return;
        if (e.Status == PluginUpdateStatus.Unchanged) { RunOnUi(() => ErrorBanner.IsOpen = false); return; }
        RunOnUi(() => ShowNotice(e.Status == PluginUpdateStatus.RuntimePending ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            e.Status == PluginUpdateStatus.RuntimePending ? "更新待应用" : "自动更新完成",
            e.Status == PluginUpdateStatus.RuntimePending ? "运行代码／配置／权限已变化；旧实例继续运行，尚未应用更新。" :
                $"{(e.Status == PluginUpdateStatus.UiApplied ? "UI" : "插件")} 已于 {e.Timestamp:HH:mm:ss} 自动更新", autoHide: e.Status != PluginUpdateStatus.RuntimePending));
    }

    private void OnReloadFailed(object? sender, HotReloadFailedEventArgs e)
    {
        if (!string.Equals(e.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;
        RunOnUi(() => ShowNotice(InfoBarSeverity.Error, "热重载失败", e.Error.ToString()));
    }

    private void OnCommandFailed(object? sender, Exception error) =>
        ShowNotice(InfoBarSeverity.Error, "命令执行失败", error.Message);

    private void ShowNotice(InfoBarSeverity severity, string title, string message, bool autoHide = false)
    {
        _noticeTimer?.Cancel();
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        ErrorBanner.Severity = severity;
        ErrorBanner.Title = title;
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
        if (!autoHide) return;
        var timer = new CancellationTokenSource();
        _noticeTimer = timer;
        _ = HideNoticeAsync(timer.Token);
    }

    private async Task HideNoticeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            RunOnUi(() =>
            {
                if (!cancellationToken.IsCancellationRequested) ErrorBanner.IsOpen = false;
            });
        }
        catch (OperationCanceledException) { }
    }

    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    private void Clear()
    {
        _viewRevision++;
        DynamicContent.Children.Clear();
        if (_factory is null) return;
        _factory.CommandFailed -= OnCommandFailed;
        _factory.Dispose();
        _factory = null;
    }

    private void DetachHotReload()
    {
        _noticeTimer?.Cancel();
        _noticeTimer?.Dispose();
        _noticeTimer = null;
        _hotReloadRegistration?.Dispose();
        _hotReloadRegistration = null;
        if (_hotReload is not null)
        {
            _hotReload.Reloaded -= OnReloaded;
            _hotReload.ReloadFailed -= OnReloadFailed;
        }
        _hotReload = null;
        _pluginId = null;
    }
}
