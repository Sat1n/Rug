using System.Reflection;
using Rug.UI.Helpers;
using Rug.UI.ViewModels;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static class ShutdownTests
{
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task RunAsync()
    {
        var entered=Signal();var release=Signal();int closed=0,tail=0;
        var sequence=new HostShutdownSequence();
        var first=sequence.RunAsync(new (string,Func<Task>)[]{
            ("actual",async()=>{Interlocked.Increment(ref closed);entered.SetResult();await release.Task;throw new IOException("controlled cleanup failure");}),
            ("remaining",()=>{tail++;return Task.CompletedTask;})},_=>{});
        await entered.Task;
        var repeated=sequence.RunAsync([], _=>throw new Exception("must not run"));
        Check(ReferenceEquals(first,repeated)&&!first.IsCompleted&&tail==0,"duplicate close did not share real cleanup");
        release.SetResult();var errors=await first;
        Check(closed==1&&tail==1&&errors.Count==1&&errors[0].Contains("IOException"),"cleanup failure skipped other steps or was hidden");
        int sibling=0;
        try{HostShutdownSequence.RunActions(()=>throw new IOException("unsubscription failure"),()=>sibling++);throw new Exception("cleanup failure hidden");}
        catch(AggregateException){Check(sibling==1,"failed unsubscription skipped a sibling cleanup");}
        foreach(bool startBarrier in new[]{false,true})await PreviewAsync(startBarrier);
        await NotificationAsync();
        Console.WriteLine("Host shutdown passed: shared close, real-work barrier, failure continuation; actual DevTools start/capture races, no late frame, no unused input service access");
    }
    private static async Task NotificationAsync()
    {
        var manager=Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
        manager.Reset();Microsoft.UI.Xaml.Controls.ContentDialog.Created=0;
        var service=new Rug.UI.Notifications.AppNotificationService(new Navigation());
        service.Initialize();service.Initialize();
        for(int i=0;i<100;i++)manager.Raise();
        Check(Rug.UI.App.MainWindow.DispatcherQueue.Count==1,"notification callbacks queued without bound");
        await service.ShutdownAsync();Rug.UI.App.MainWindow.DispatcherQueue.Drain();service.Dispose();
        Check(manager.Registers==1&&manager.Unregisters==1&&manager.Subscribers==0&&Microsoft.UI.Xaml.Controls.ContentDialog.Created==0&&!service.Show("ignored"),"queued notification ran after shutdown or registered twice");
        manager.Reset();service=new(new Navigation());service.Initialize();
        var dispatcher=Rug.UI.App.MainWindow.DispatcherQueue;using var release=new ManualResetEventSlim();dispatcher.Hold=release;dispatcher.Entered=Signal();
        var callback=Task.Run(manager.Raise);await dispatcher.Entered.Task;
        var retiring=service.ShutdownAsync();Check(!retiring.IsCompleted,"shutdown did not drain entered background callback");
        release.Set();await callback;await retiring;dispatcher.Hold=null;dispatcher.Entered=null;dispatcher.Drain();
        Check(Microsoft.UI.Xaml.Controls.ContentDialog.Created==0,"inflight old callback touched UI after producer retirement");
        manager.Reset();service=new(new Navigation());service.Initialize();manager.Raise();Rug.UI.App.MainWindow.DispatcherQueue.Drain();
        var close=service.ShutdownAsync();var same=service.ShutdownAsync();
        Check(ReferenceEquals(close,same)&&!close.IsCompleted&&Microsoft.UI.Xaml.Controls.ContentDialog.Last!.Hidden,"entered notification work not drained/shared");
        Microsoft.UI.Xaml.Controls.ContentDialog.Last!.Finish();await close;
        Check(manager.Unregisters==1&&manager.Subscribers==0,"notification removal not paired");
        manager.Reset();manager.FailUnregister=true;service=new(new Navigation());service.Initialize();
        try{await service.ShutdownAsync();throw new Exception("unregister failure hidden");}catch(AggregateException){}
        Check(manager.Unregisters==1&&manager.Subscribers==0,"unregister failure skipped event removal");
        manager.Reset();
        Console.WriteLine("Notification lifecycle passed: actual service source, bounded queued callback, close-before-delivery, active dialog drain, paired registration, duplicate cleanup and visible failure (WinRT doubles)");
    }
    private sealed class Navigation : Rug.UI.Contracts.Services.INavigationService;
    private static async Task PreviewAsync(bool startBarrier)
    {
        var capture=new BarrierCapture(startBarrier);
        var vm=new DevToolsViewModel(capture,DispatchProxy.Create<IWindowSpyService,Unused>(),new Ocr(),DispatchProxy.Create<IInputService,Unused>(),new InputArbiter(),DispatchProxy.Create<IInputInterventionCoordinator,Unused>())
        {BoundWindow=new(1,"owned","fixture",32,32,1,32,32,"fixture",0,0,100,100),IsOcrOverlayEnabled=false};
        int frames=0;vm.FrameReady+=(_,_)=>frames++;
        var start=vm.StartPreviewAsync();await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var close=vm.ShutdownAsync();var duplicate=vm.ShutdownAsync();
        Check(ReferenceEquals(close,duplicate)&&!close.IsCompleted&&capture.Stops==0&&capture.IsCapturing,"close released active capture/start early");
        capture.Release.SetResult();await start;var errors=await close.WaitAsync(TimeSpan.FromSeconds(3));
        Check(errors.Count==0&&frames==0&&!capture.IsCapturing&&capture.Stops==1,"late preview published or capture cleanup failed");
        int starts=capture.Starts;await vm.StartPreviewAsync();Check(capture.Starts==starts,"preview restarted after host closing");
    }
    public class Unused : DispatchProxy
    { protected override object? Invoke(MethodInfo? targetMethod,object?[]? args)=>throw new InvalidOperationException("Unused service accessed: "+targetMethod?.Name); }
    private sealed class BarrierCapture(bool atStart) : ICaptureService
    {
        internal TaskCompletionSource Entered=Signal(),Release=Signal();internal int Starts,Stops;
        public bool IsCapturing{get;private set;}
        public async Task StartAsync(nint hwnd,CancellationToken cancellationToken=default)
        {Starts++;IsCapturing=true;if(atStart){Entered.SetResult();await Release.Task;}}
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken=default)
        {Entered.TrySetResult();await Release.Task;return new(new byte[4],1,1,4);}
        public Task StopAsync(){Stops++;IsCapturing=false;return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Ocr : IOcrService
    {
        public IReadOnlyList<string> ListPaddleModelIds()=>[];
        public Task<IReadOnlyList<OcrTextBlock>> RecognizeAsync(string path,OcrEngineType engineType,string? modelId=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<IReadOnlyList<OcrTextBlock>> RecognizeFrameAsync(CapturedFrame frame,OcrEngineType engineType,string? modelId=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
