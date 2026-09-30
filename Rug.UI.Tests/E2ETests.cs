using System.Diagnostics;

public static class E2ETests
{
    public static async Task RunAsync()
        => await RunScriptAsync("Phase3E2E.ps1", "Phase 3 UI E2E");

    public static async Task RunInstanceUiAsync()
        => await RunScriptAsync("GenericInstanceUiSmoke.ps1", "Generic instance UI E2E");

    public static async Task RunTakeoverUiAsync()
        => await RunScriptAsync("ManualTakeoverUiSmoke.ps1", "Manual takeover UI E2E");

    private static async Task RunScriptAsync(string scriptName, string description)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.UI", "Rug.UI.csproj")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Rug workspace root was not found.");
        string script = Path.Combine(root.FullName, "Rug.UI.Tests", scriptName);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                ArgumentList = { "-NoProfile", "-File", script },
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(description + " exceeded 90 seconds.");
        }
        string output = await stdout;
        string error = await stderr;
        Console.WriteLine(output);
        if (process.ExitCode != 0) throw new Exception(description + " failed: " + error);
    }
}
