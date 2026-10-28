using System.Text.Json;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

/// <summary>Bounded read-only producer audit, not an input/scene acceptance test.
/// @shape rows: at most 128 detached native facts; no pixels, fabricated observation IDs or retries.
/// @source capture: Rug.UI.Core/Services/CaptureService.cs#class:CaptureService</summary>
internal static class CaptureTimeProbe
{
    public static async Task RunAsync(string output)
    {
        string repo=ControlledRecoverySample.Repo();ControlledRecoverySample.LoadNative(repo);
        string root=Path.Combine(Path.GetFullPath(output),"capture-time-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        using var target=new CaptureGeometryDiagnosticWindow();nint hwnd=await target.Ready;
        await using var capture=new CaptureService(exactBoundWindow:true);
        var clock=new ObservedInputEnvironment();var rows=new List<object>();int unknown=0,future=0,missing=0;
        long? Age(long? time,long? frequency,QpcReading current)=>time is >0&&current.Frequency>0&&current.Ticks>0&&frequency==current.Frequency
            ?checked((long)((Int128)current.Ticks*TimeSpan.TicksPerSecond/current.Frequency-time.Value)):null;
        await capture.StartAsync(hwnd);
        try
        {
            for(int index=0;index<128;index++)
            {
                var requested=clock.ReadQpc();var frame=await capture.GrabFrameAsync();var received=clock.ReadQpc();
                if(frame is null){missing++;rows.Add(new{index,noFrame=true});continue;}
                var raw=frame.NativeCaptureFacts;
                long? nativeAge=raw is {} f?Age(f.SystemRelative100ns,f.QpcFrequency,new(f.QpcSample,f.QpcFrequency)):null;
                if(frame.CaptureFacts?.NativeTime is null)unknown++;
                if(nativeAge is <0)future++;
                rows.Add(new{index,frame.Width,frame.Height,requestedQpc=requested.Ticks,currentQpc=received.Ticks,currentFrequency=received.Frequency,
                    clockDomain=received.Domain,systemRelative100ns=raw?.SystemRelative100ns,nativeSampleQpc=raw?.QpcSample,nativeFrequency=raw?.QpcFrequency,
                    nativeFlags=raw?.Flags,geometryStatus=raw?.GeometryStatus,managedTimeAvailable=frame.CaptureFacts?.NativeTime is not null,
                    signedAgeAtNative100ns=nativeAge,signedAgeAtReceipt100ns=Age(raw?.SystemRelative100ns,raw?.QpcFrequency,received)});
            }
        }
        finally{await capture.StopAsync();}
        ControlledRecoverySample.Check(!capture.IsCapturing&&target.ClickCounts==(0,0),"time audit sent input or retained capture");
        await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{schemaVersion=1,configuration=ControlledRecoverySample.Config,
            kind="read-only native producer sampling; not business acceptance",samples=128,noFrame=missing,managedTimeUnavailable=unknown,futureAtNativeSample=future,
            receivedDown=target.ClickCounts.Down,receivedUp=target.ClickCounts.Up,captureStopped=!capture.IsCapturing,rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"CAPTURE TIME: unknown={unknown}, future-at-native={future}, no-frame={missing}, input=0/0; {Path.Combine(root,"report.json")}");
    }
}
