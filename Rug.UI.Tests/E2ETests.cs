using System.Diagnostics;

public static class E2ETests
{
    public static async Task RunAsync()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Rug.UI", "Rug.UI.csproj")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Rug workspace root was not found.");
        string script = Path.Combine(root.FullName, "Rug.UI.Tests", "Phase3E2E.ps1");
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
            throw new TimeoutException("Phase 3 UI E2E exceeded 90 seconds.");
        }
        string output = await stdout;
        string error = await stderr;
        Console.WriteLine(output);
        if (process.ExitCode != 0) throw new Exception("Phase 3 UI E2E failed: " + error);
    }
}
