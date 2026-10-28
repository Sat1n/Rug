#nullable enable
using System.Runtime.InteropServices;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;
namespace Rug.UI.Core.Native;

/// <summary>Production trusted current-QPC and window checks. No script data becomes capture facts.</summary>
public sealed class ObservedInputEnvironment : IObservedInputEnvironment
{
    public QpcReading ReadQpc()
    {
        if(!OperatingSystem.IsWindows() || !QueryPerformanceCounter(out long ticks) || !QueryPerformanceFrequency(out long frequency))
            throw new ObservedClickException(ObservedClickReason.TimeUnavailable);
        return new(ticks,frequency);
    }
    public void ValidateGeometry(RunIdentity run, CapturedFrame frame)
    {
        if(frame.NativeCaptureFacts is not { } facts) throw new ObservedClickException(ObservedClickReason.GeometryChanged);
        int status=RugCoreNative.Rug_CheckCaptureGeometry(run.TargetHwnd,in facts);
        if(status==RugStatus.ErrWindowIdentity) throw new ObservedClickException(ObservedClickReason.WindowInvalid);
        if(status!=0) throw new ObservedClickException(ObservedClickReason.GeometryChanged);
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryPerformanceCounter(out long counter);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryPerformanceFrequency(out long frequency);
}
