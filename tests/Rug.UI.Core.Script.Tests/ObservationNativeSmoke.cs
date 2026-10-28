using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Contracts.Services;
using Rug.UI.Core.Models;
using Rug.UI.Core.Native;
using Rug.UI.Core.Services;

internal static class ObservationNativeSmoke
{
    // Trusted test host only: creates a small ephemeral matching fixture from the actual captured frame.
    // No script/result receives this buffer, and the temporary BMP is deleted on exit.
    private sealed class TemplateCapture(string template) : ICaptureService, IBoundedFrameCapture
    {
        private readonly CaptureService _capture=new();
        public bool IsCapturing=>_capture.IsCapturing;
        public Task StartAsync(nint hwnd,CancellationToken cancellationToken=default)=>_capture.StartAsync(hwnd,cancellationToken);
        public async Task<CapturedFrame?> GrabFrameAsync(CancellationToken cancellationToken=default)
            => await GrabFrameBoundedAsync(64L*1024*1024,cancellationToken);
        public async Task<CapturedFrame?> GrabFrameBoundedAsync(long maxBytes,CancellationToken cancellationToken)
        {
            var frame=await ((IBoundedFrameCapture)_capture).GrabFrameBoundedAsync(maxBytes,cancellationToken);
            if(frame is not null) WriteBmp(template,frame);
            return frame;
        }
        public Task StopAsync()=>_capture.StopAsync();
        public ValueTask DisposeAsync()=>_capture.DisposeAsync();
    }
    private static void WriteBmp(string path,CapturedFrame frame)
    {
        int width=Math.Min(220,frame.Width-12),height=Math.Min(80,frame.Height);
        int bytes=checked(width*height*4);var bmp=new byte[54+bytes];bmp[0]=66;bmp[1]=77;
        void Int(int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(offset,4),value);
        Int(2,bmp.Length);Int(10,54);Int(14,40);Int(18,width);Int(22,-height);bmp[26]=1;bmp[28]=32;Int(34,bytes);
        for(int y=0;y<height;y++)Buffer.BlockCopy(frame.Pixels,y*frame.Stride+12*4,bmp,54+y*width*4,width*4);
        File.WriteAllBytes(path,bmp);
    }
    public static async Task RunAsync()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"Rug.slnx")))repo=repo.Parent;
        string dll=Path.Combine(repo!.FullName,"x64","Debug","Rug.Core.dll");
        if(!OperatingSystem.IsWindows()||!File.Exists(dll)){Console.WriteLine("Observation native NOT RUN: Windows/Debug x64 Rug.Core unavailable");return;}
        var assembly=typeof(CaptureService).Assembly;
        nint module=NativeLibrary.Load(dll,assembly,DllImportSearchPath.UseDllDirectoryForDependencies);
        NativeLibrary.SetDllImportResolver(assembly,(name,_,_)=>name is "Rug.Core" or "Rug.Core.dll"?module:0);
        string root=Path.Combine(AppContext.BaseDirectory,"observation-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var pool=new ObservationFramePool();
        try
        {
            using var target=new RecoveryDiagnosticWindow();nint hwnd=await target.Ready;target.SetReady(true);
            await using var capture=new TemplateCapture(Path.Combine(root,"template.bmp"));await capture.StartAsync(hwnd);
            try{await capture.GrabFrameBoundedAsync(4,CancellationToken.None);throw new Exception("native small-limit ingress was not rejected");}
            catch(ObservationCapacityException){} // Real native buffer freed; no managed frame allocation/publication.
            var run=new RunIdentity("observation-native",Guid.NewGuid(),1,Guid.NewGuid(),hwnd);
            var manifest=new PluginManifest([Permission.Capture,Permission.VisionOcr,Permission.VisionMatch]){Id=run.PluginId,PluginDirectory=root};
            using(var bridge=new VisualEngineBridge(capture,new OcrService(),new TemplateMatchService(),
                TestAuthorizationHost.Gate(manifest,NullLogger.Instance),run,root,pool))
            {
                CapturedFrameMetadata? frame=null;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while(frame is null){frame=await bridge.CaptureAsync(timeout.Token);if(frame is null)await Task.Delay(16,timeout.Token);}
                var ocr=await bridge.OcrAsync(language:"en-US",cancellationToken:timeout.Token);
                var match=await bridge.FindImageAsync("template.bmp",.95f,cancellationToken:timeout.Token);
                RecoveryTests.Check(ocr.Blocks.Count>0 && match is not null,"real WGC/OCR/matching produced no recognition");
                RecoveryTests.Check(ocr.Observation.FrameId==frame.Observation.FrameId && match!.Observation.FrameId==frame.Observation.FrameId &&
                    ocr.Observation.ObservationId!=match.Observation.ObservationId && ocr.Observation.Run==ObservationRun.From(run) &&
                    frame.Observation.Time.NativeAcquiredUtc is null && frame.Observation.Time.NativeTime?.AgeAtNativeSample is not null &&
                    frame.Observation.CaptureFacts?.ContentWidth>0,"native result provenance invalid");
                Console.WriteLine($"Observation native PASS: real WGC -> WinRT OCR blocks={ocr.Blocks.Count} / OpenCV match; frameId={frame.Observation.FrameId}; independent observations, exact host run/binding; actual native compositor time retained; geometry status={frame.Observation.CaptureFacts?.GeometryStatus}");
            }
            await capture.StopAsync();RecoveryTests.Check(!capture.IsCapturing && pool.Usage==new ObservationUsage(0,0,0,0,0),"native observation cleanup leaked");
        }
        finally{Directory.Delete(root,true);}
    }
}
