using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

namespace Rug.UI.Views;

// Supplies only the generated fields/events; all selection and rendering handlers
// under test come from the production TraceViewerPage.xaml.cs linked by the project.
public sealed partial class TraceViewerPage
{
    private readonly TextBlock Health=new(),TraceStatus=new(),IncidentStatus=new();
    private readonly TextBox Detail=new(),OperationFilter=new();
    private readonly Button MoreCatalog=new(),MoreEvents=new(),MoreIncidents=new(),Locate=new();
    private readonly ListView Traces=new(),Events=new(),Incidents=new();
    private void InitializeComponent()
    {
        Traces.SelectionChanged+=Trace_Changed;
        Events.SelectionChanged+=Event_Changed;
        Incidents.SelectionChanged+=Incident_Changed;
    }
    private sealed class BarrierReader(bool hold) : ITraceReviewReader
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TraceChoice Choice=new(Guid.NewGuid(),Guid.NewGuid(),null,true,"test",true);
        internal readonly ExecutionTraceEvent Row=new(1,Guid.NewGuid(),Guid.NewGuid(),7,null,null,null,null,null,
            null,null,"selected-event",null,TraceEventKind.Dispatch,DateTimeOffset.UtcNow,1,1,"test",
            null,null,null,null,null,null,TraceDataState.NotApplicable,TraceDataState.NotApplicable);
        public TraceReviewResult Read(TraceReviewRequest request)
        {
            if(request.Kind==TraceReviewKind.Incident)
            {
                Entered.TrySetResult();if(hold)Release.Task.GetAwaiter().GetResult();
                return new(request,null,Incident:new(request.IncidentKey!,IncidentReadStatus.Corrupt,null));
            }
            if(request.Kind==TraceReviewKind.Catalog)return new(request,null,Choices:[Choice]);
            if(request.Kind==TraceReviewKind.Incidents)return new(request,null,Incidents:new([],null,false));
            return new(request,null,Page:new(TraceReadStatus.Incomplete,[Row],8,null,false));
        }
    }
    public static async Task RunSelectionRegressionAsync()
    {
        foreach(bool hold in new[]{true,false})
        {
            var reader=new BarrierReader(hold);using var broker=new TraceReviewService(reader);
            App.TestService=broker;var page=new TraceViewerPage();page.Load();
            async Task DrainRead()
            {
                await Until(()=>!broker.Usage.Reading);page.DispatcherQueue.Drain();
            }
            try
            {
                await DrainRead();page.Traces.SelectedIndex=0;await DrainRead();
                page.Incidents.ItemsSource=new[]{new ListViewItem{Tag=new IncidentEntry("old.json",IncidentReadStatus.Valid,null,null,null,null,null,false)}};
                page.Incidents.SelectedIndex=0;await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                if(!hold)await Until(()=>!broker.Usage.Reading&&page.DispatcherQueue.Count>0);
                page.Events.SelectedIndex=0;
                string selected=page.Detail.Text;
                Check(selected.Contains("selected-event"),"event selection did not render its own detail");
                reader.Release.TrySetResult();await DrainRead();
                Check(page.Detail.Text==selected&&page.Events.SelectedIndex==0&&page.Incidents.SelectedIndex==-1,
                    "late incident read/queued delivery overwrote the selected event");
                page.Received(new(new(TraceReviewKind.Incident,IncidentKey:"old.json"),null,
                    Incident:new("old.json",IncidentReadStatus.Corrupt,null)));
                Check(page.Detail.Text==selected,"detail ownership accepted an already-entered stale callback");
                Check(!page.Locate.IsEnabled,"event retained stale incident navigation");
                page.Events.SelectedIndex=-1;Check(page.Detail.Text=="","cleared event kept stale detail");
                page.Events.SelectedIndex=0;page.MoreEvents_Click(page,new());
                Check(page.Detail.Text=="","event page request retained previous detail");
                await DrainRead();Check(page.Detail.Text==""&&page.Events.SelectedIndex==-1,"page replacement retained old detail/selection");
                page.Events.SelectedIndex=0;page.Catalog_Click(page,new());await DrainRead();
                Check(page.Detail.Text==""&&page.Events.SelectedIndex==-1,"catalog replacement retained old event detail");
                page.Traces.SelectedIndex=0;await DrainRead();page.Events.SelectedIndex=0;
                page.Incidents_Click(page,new());await DrainRead();
                Check(page.Detail.Text=="","incident list refresh retained old detail");
                page.Incidents.ItemsSource=new[]{new ListViewItem{Tag=new IncidentEntry("new.json",IncidentReadStatus.Corrupt,null,null,null,null,null,false)}};
                page.Incidents.SelectedIndex=0;await DrainRead();
                Check(page.Detail.Text=="Corrupt"&&page.Events.SelectedIndex==-1,"current incident detail did not replace event ownership");
                page.Incidents.SelectedIndex=-1;Check(page.Detail.Text=="","cleared incident kept stale detail");
            }
            finally{reader.Release.TrySetResult();page.Unload();App.TestService=null;}
        }
        Console.WriteLine("Actual trace page selection regression passed: blocked/queued incident cannot overwrite event; clear/page/catalog discard stale details");
    }
    private static async Task Until(Func<bool> ready)
    {
        var watch=Stopwatch.StartNew();
        while(!ready()){if(watch.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("Trace page barrier");await Task.Yield();}
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
