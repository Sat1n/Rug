namespace Rug.UI.Helpers;

/// <summary>One shared close result; stages run outside the gate on the caller's UI context.
/// @shape stages: fixed host-owned actions; result: detached stage/error summaries.
/// @source owner: MainWindow.xaml.cs#class:MainWindow</summary>
public sealed class HostShutdownSequence
{
    public static void RunActions(params Action[] actions)
    {
        var errors=new List<Exception>();
        foreach(var action in actions)try{action();}catch(Exception error){errors.Add(error);}
        if(errors.Count>0)throw new AggregateException(errors);
    }
    private readonly object _gate = new();
    private TaskCompletionSource<IReadOnlyList<string>>? _completion;
    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<(string Name, Func<Task> Run)> stages, Action<string> report)
    {
        TaskCompletionSource<IReadOnlyList<string>> completion;
        lock (_gate)
        {
            if (_completion is not null) return _completion.Task;
            completion = _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunCoreAsync();
        return completion.Task;
        async Task RunCoreAsync()
        {
            var errors = new List<string>();
            void Report(string message) { try { report(message); } catch { /* Diagnostics cannot skip cleanup. */ } }
            foreach (var stage in stages)
            {
                Report("shutdown:start:" + stage.Name);
                try { await stage.Run(); Report("shutdown:complete:" + stage.Name); }
                catch (Exception error) { string message = stage.Name + ": " + error.GetType().Name + ": " + error.Message; errors.Add(message); Report("shutdown:failed:" + message); }
            }
            completion.TrySetResult(Array.AsReadOnly(errors.ToArray()));
        }
    }
}
