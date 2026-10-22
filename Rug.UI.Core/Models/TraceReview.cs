#nullable enable
namespace Rug.UI.Core.Models;

public enum TraceReviewKind { Catalog, Memory, Disk, Incidents, Incident }
public sealed record TraceReviewRequest(TraceReviewKind Kind, Guid? SessionId=null,Guid? TraceId=null,
    Guid? OperationId=null,long AfterSequence=0,int Offset=0,string? IncidentKey=null,int Limit=100);
public sealed record TraceChoice(Guid SessionId,Guid TraceId,TraceRun? Run,bool Memory,string Integrity,bool? IsControl=null)
{
    public string Label=>$"{(Memory?"当前保留窗口":"文件")} · {(Run is null?IsControl==true?"control":"运行身份未知":Run.PluginId+" / "+Run.InstanceId.ToString("N")+" g"+Run.RunGeneration)} · session {SessionId:N} · trace {TraceId:N} · {Integrity}";
}
public sealed record TraceReviewResult(TraceReviewRequest Request,TraceDiagnostics? Health,
    IReadOnlyList<TraceChoice>? Choices=null,int? NextOffset=null,TracePage? Page=null,
    IncidentPage? Incidents=null,IncidentDetail? Incident=null,string? Error=null,bool? RecorderEnabled=null);
