using System.Text.Json;
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

internal static class HostExitMatrix
{
    public static async Task RunAsync()
    {
        string repo=ControlledRecoverySample.Repo();ControlledRecoverySample.LoadNative(repo);
        string root=Path.Combine(repo,"artifacts","host-exit","ui-"+ControlledRecoverySample.Config+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var rows=new List<object>();bool passed=true;
        foreach(string phase in new[]{"Preview","Active","Awaiting"})
        {
            string area=Path.Combine(root,phase);
            var result=await ControlledRecoveryUi.RunAsync(repo,area,exitPhase:phase);
            bool valid=result.InteractionsPassed&&result.ExitCode==0&&string.IsNullOrEmpty(result.Stderr)&&result.TargetDown==(phase=="Preview"?0:1)&&result.TargetDown==result.TargetUp;
            var summaries=new List<TraceSummary>();
            foreach(string path in Directory.GetFiles(Path.Combine(area,"traces"),"*.summary").Take(128))
            {
                using var doc=JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var summary=doc.RootElement.GetProperty("payload").Deserialize<TraceSummary>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
                if(summary.Run is null)continue;
                var page=new TraceFileStore(Path.Combine(area,"traces")).Read(summary.HostSessionId,summary.TraceId,limit:256);
                valid&=page.Status==TraceReadStatus.Valid&&summary is {RunCleanupCompleted:true,CleanupSucceeded:true,ActualWorkOutstanding:0};summaries.Add(summary);
            }
            valid&=summaries.Count==(phase=="Preview"?0:1);
            passed&=valid;rows.Add(new{phase,passed=valid,result,runCleanup=summaries});
            Console.WriteLine($"EXIT {phase}: passed={valid}, target={result.TargetDown}/{result.TargetUp}, exit={result.ExitCode}");
        }
        await File.WriteAllTextAsync(Path.Combine(root,"report.json"),JsonSerializer.Serialize(new{configuration=ControlledRecoverySample.Config,passed,scenarios=rows},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("EXIT MATRIX "+Path.Combine(root,"report.json"));if(!passed)throw new Exception("Exit scenario matrix failed");
    }
}
