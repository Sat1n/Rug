using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
using Rug.UI.Helpers;

namespace Rug.UI.Views;

/// <summary>One bounded page replaces, rather than accumulates, query results. No live subscriptions.</summary>
public sealed partial class TraceViewerPage : Page
{
    private TraceReviewService.Session? _session;
    private bool _active,_setting;
    private int? _nextCatalog,_nextIncidents;
    private long? _nextEvents;
    private IncidentDetail? _incident;
    private IncidentTraceLink? _locating;
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,Converters={new JsonStringEnumConverter()}};
    private sealed record EventItem(ExecutionTraceEvent Event)
    {
        public string Label=>$"#{Event.Sequence} · {Event.Utc:u} · {Event.Kind} · {Event.Operation??"—"} · execution {Event.ExecutionId?.ToString()??"未采集"} · {Event.Status??"—"} · OutcomeUncertain={Uncertain(Event.OutcomeUncertain)}";
    }
    private sealed record IncidentItem(IncidentEntry Entry)
    {
        public string Label=>$"{Entry.Status} · {Entry.PluginId??"未知插件"} · {Entry.InstanceId?.ToString()??"未知实例"} · {Entry.Timestamp:u} · {Entry.IncidentId?.ToString()??"无新格式身份"} · {Entry.Reason??Entry.FileKey}";
    }
    public TraceViewerPage()
    {
        InitializeComponent();Loaded+=Open;Unloaded+=(_,_)=>CloseQueries();
    }
    private void Open(object sender,RoutedEventArgs args)
    {
        if(_active)return;_active=true;
        var queue=DispatcherQueue;
        _session=App.GetService<TraceReviewService>().Open(action=>queue.TryEnqueue(()=>action()));
        _session.Completed+=Received;
        Submit(new(TraceReviewKind.Catalog));
    }
    public void CloseQueries()
    {
        _active=false;_session?.Dispose();_session=null;
        // No wait/cancellation claim for the broker's real physical read.
    }
    private void Submit(TraceReviewRequest request)
    {
        if(!_active)return;
        TraceStatus.Text="查询中（最多一个后台实际读取；旧磁盘读取可能仍在进行）…";
        _session?.Submit(request);
    }
    private void Catalog_Click(object sender,RoutedEventArgs e)=>Submit(new(TraceReviewKind.Catalog));
    private void MoreCatalog_Click(object sender,RoutedEventArgs e){if(_nextCatalog is {} n)Submit(new(TraceReviewKind.Catalog,Offset:n));}
    private void Trace_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_setting)return;_locating=null;_session?.Invalidate();Events.ItemsSource=null;Detail.Text="";MoreEvents.IsEnabled=false;
        ReadTrace(0);
    }
    private void Refresh_Click(object sender,RoutedEventArgs e)=>ReadTrace(0);
    private void Filter_Changed(object sender,TextChangedEventArgs e)
    {if(_setting)return;_session?.Invalidate();if(MoreEvents is not null)MoreEvents.IsEnabled=false;_nextEvents=null;if(Events is not null)Events.ItemsSource=null;if(Detail is not null)Detail.Text="";}
    private void MoreEvents_Click(object sender,RoutedEventArgs e){if(_nextEvents is {} n)ReadTrace(n);}
    private void ReadTrace(long after)
    {
        ClearDetail();
        if((Traces.SelectedItem as ListViewItem)?.Tag is not TraceChoice choice)return;
        Guid? operation=null;
        if(!string.IsNullOrWhiteSpace(OperationFilter.Text))
        {if(!Guid.TryParse(OperationFilter.Text,out var id)){TraceStatus.Text="operationId 必须是 GUID。";_session?.Invalidate();return;}operation=id;}
        Submit(new(choice.Memory?TraceReviewKind.Memory:TraceReviewKind.Disk,choice.SessionId,choice.TraceId,operation,after));
    }
    private void Event_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_setting)return;
        _session?.Invalidate();ClearDetail();
        _setting=true;Incidents.SelectedIndex=-1;_setting=false;
        if((Events.SelectedItem as ListViewItem)?.Tag is ExecutionTraceEvent row)Show(JsonSerializer.Serialize(row,Json));
    }
    private void Incidents_Click(object sender,RoutedEventArgs e)=>Submit(new(TraceReviewKind.Incidents));
    private void MoreIncidents_Click(object sender,RoutedEventArgs e){if(_nextIncidents is {} n)Submit(new(TraceReviewKind.Incidents,Offset:n));}
    private void Incident_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_setting)return;_session?.Invalidate();ClearDetail();
        _setting=true;Events.SelectedIndex=-1;_setting=false;
        if((Incidents.SelectedItem as ListViewItem)?.Tag is IncidentEntry item)Submit(new(TraceReviewKind.Incident,IncidentKey:item.FileKey));
    }
    private void Locate_Click(object sender,RoutedEventArgs e)
    {
        var link=_incident?.Record?.TraceLink;
        if(link?.HostSessionId is not {} session||link.TraceId is not {} trace)return;
        _locating=link;
        var choice=new TraceChoice(session,trace,link.Run,false,"异常关联 · "+link.Status);
        _setting=true;Traces.ItemsSource=new[]{TraceItem(choice)};Traces.SelectedIndex=0;_setting=false;
        _setting=true;OperationFilter.Text=link.OperationId?.ToString()??"";_setting=false;
        // Boundary upper bound is not an exact fault event. Show a bounded neighborhood.
        long after=Math.Max(0,(link.SequenceUpperBound??1)-40);
        ReadTrace(after);
    }
    private void Received(TraceReviewResult result)
    {
        if(!_active)return;
        // The broker revision rejects queued reads; selection ownership also guards
        // the shared detail pane, including callbacks already entering delivery.
        if(result.Incident is {} incoming&&
            (Events.SelectedItem is not null||(Incidents.SelectedItem as ListViewItem)?.Tag is not IncidentEntry owner||owner.FileKey!=incoming.FileKey))return;
        try
        {
            var h=result.Health;
            Health.Text=h is null?"本次健康信息未采集":$"记录器启用={Uncertain(result.RecorderEnabled)}；关闭中={h.Closing}；丢失 {h.DroppedEvents}；写入失败 {h.WriteFailures}；待写 {h.QueuedEvents} / {h.QueuedBytes}B；关闭刷新不完整 {h.FlushIncomplete}；writer忙 {h.WriterBusy}；{h.LastWriterError??"无已知写入错误"}";
            if(result.Error is {} error){TraceStatus.Text=error;return;}
            if(result.Choices is {} choices)
            {
                ClearDetail();
                _setting=true;
                Events.ItemsSource=null;
                Traces.ItemsSource=choices.Select(TraceItem).ToArray();Traces.SelectedIndex=-1;_setting=false;
                _nextCatalog=result.NextOffset;MoreCatalog.IsEnabled=_nextCatalog is not null;
                MoreEvents.IsEnabled=false;TraceStatus.Text=$"本页 {choices.Count} 个轨迹选项；请选择当前窗口或历史文件。";
            }
            if(result.Page is {} page)
            {
                ClearDetail();
                _nextEvents=page.NextSequence;MoreEvents.IsEnabled=_nextEvents is not null;
                _setting=true;
                Events.ItemsSource=page.Events.OrderBy(x=>x.Sequence).Select(x=>new ListViewItem{Content=new EventItem(x).Label,Tag=x}).ToArray();
                _setting=false;
                TraceStatus.Text=$"{TracePresentation.Integrity(page)} · 本页 {page.Events.Count} 条；任务结果另见事件。"+
                    $" 已知丢失 {page.Summary?.DroppedEvents.ToString()??"未知"}；session丢失 {page.SessionDroppedEvents?.ToString()??"未确认"}；清理尝试 {page.Summary?.RunCleanupCompleted.ToString()??"未知"}；清理成功 {Uncertain(page.Summary?.CleanupSucceeded)}。";
                if(_locating is not null)
                    TraceStatus.Text+=$" 异常关联上界 #{_locating.SequenceUpperBound?.ToString()??"未知"}（不是精确故障瞬间）。"+
                        (page.Status==TraceReadStatus.NotFound?" 关联轨迹不可用；异常本身仍可查看。":"");
            }
            if(result.Incidents is {} incidents)
            {
                ClearDetail();
                _setting=true;Incidents.ItemsSource=incidents.Entries.Select(x=>new ListViewItem{Content=new IncidentItem(x).Label,Tag=x}).ToArray();Incidents.SelectedIndex=-1;_setting=false;
                _nextIncidents=incidents.NextOffset;MoreIncidents.IsEnabled=_nextIncidents is not null;
                _incident=null;Locate.IsEnabled=false;
                IncidentStatus.Text=$"本页异常 {incidents.Entries.Count} 条；扫描上限 {IncidentStore.MaxScan}；达到扫描上限={incidents.ScanLimited}。";
            }
            if(result.Incident is {} incident)
            {
                _incident=incident;var r=incident.Record;
                Locate.IsEnabled=r?.TraceLink?.HostSessionId is not null&&r.TraceLink.TraceId is not null;
                IncidentStatus.Text=$"{incident.Status} · {incident.Diagnostic??""} · "+(r?.TraceLink is {} link?$"关联 {link.Status} / 持久化 {link.Persistence}":"legacy／无关联记录，不能猜测最后一次操作。");
                Show(r is null?incident.Status.ToString():$"incidentId={r.IncidentId?.ToString()??"legacy 无新身份"}\nschemaVersion={r.SchemaVersion?.ToString()??"legacy"}\nplugin={r.PluginId}\ninstance={r.InstanceId}\nreason={r.Reason}\nscriptContext={r.ScriptContext}\nPNG引用={incident.ScreenshotReference??"引用不可用"}\n截图类型={r.ScreenshotCaptureKind??"legacy 未知"}\ntrace={JsonSerializer.Serialize(r.TraceLink,Json)}\nrecoveryId={r.RecoveryId}\nverification={JsonSerializer.Serialize(r.Verification,Json)}\nagent_resolution={JsonSerializer.Serialize(r.AgentResolution,Json)}");
            }
        }
        catch(Exception error){TraceStatus.Text="显示失败 · "+error.GetType().Name;}
    }
    private void ClearDetail()
    {_incident=null;Detail.Text="";Locate.IsEnabled=false;}
    private void Show(string text)=>Detail.Text=text.Length<=65536?text:text[..65536]+"\n[显示已截断；原文件未修改]";
    private static ListViewItem TraceItem(TraceChoice choice)=>new(){Content=choice.Label,Tag=choice};
    private static string Uncertain(bool? value)=>TracePresentation.Boolean(value);
}
