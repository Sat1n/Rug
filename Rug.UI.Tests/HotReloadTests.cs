using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Schema;
using Rug.UI.Core.Services;

internal static class HotReloadTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "hotreload-" + Guid.NewGuid().ToString("N"));
        string plugin = Path.Combine(root, "demo-folder");
        Directory.CreateDirectory(plugin);
        string uiPath = Path.Combine(plugin, "ui.json");
        string luaPath = Path.Combine(plugin, "main.lua");
        await File.WriteAllTextAsync(Path.Combine(plugin, "manifest.json"),
            """{"id":"demo","name":"Demo","version":"1","entry":"main.lua","targetProcess":"example","permissions":[]}""");
        await File.WriteAllTextAsync(uiPath, Schema("Initial"));
        await File.WriteAllTextAsync(luaPath, "value = 1\n");

        await using var scheduler = new Rug.UI.Core.Services.TaskScheduler(new PluginManager(NullLogger<PluginManager>.Instance), root,
            () => throw new Exception("No run expected"), () => throw new Exception("No run expected"), (_, _) => throw new Exception("No run expected"),
            NullLogger<Rug.UI.Core.Services.TaskScheduler>.Instance, new NoAnomaly());
        var success = Channel.CreateUnbounded<HotReloadEventArgs>();
        var failure = Channel.CreateUnbounded<HotReloadFailedEventArgs>();
        int callbackCount = 0;
        string? latestTitle = null;
        try
        {
            await using (var service = new HotReloadService(root, scheduler,
                NullLogger<HotReloadService>.Instance, TimeSpan.FromMilliseconds(350)))
            {
                service.Reloaded += (_, e) => success.Writer.TryWrite(e);
                service.ReloadFailed += (_, e) => failure.Writer.TryWrite(e);
                using var registration = service.RegisterUiHandler("demo", (snapshot, token) =>
                {
                    string title = new SchemaParser().Parse(snapshot.UiJson!).Title;
                    return Task.FromResult<IPluginUiUpdate>(new UiUpdate(() => { latestTitle = title; Interlocked.Increment(ref callbackCount); }));
                });
                service.Start();
                await service.Ready;

                var stopwatch = Stopwatch.StartNew();
                for (int i = 0; i < 5; i++)
                {
                    await File.WriteAllTextAsync(uiPath, Schema("Rapid " + i));
                    await Task.Delay(15);
                }
                HotReloadEventArgs first = await NextAsync(success.Reader);
                Check(first.PluginId == "demo" && latestTitle == "Rapid 4", "rapid save content");
                Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "reload latency");
                await Task.Delay(650);
                Check(callbackCount == 1 && !success.Reader.TryRead(out _), "one callback for five writes");

                await File.WriteAllTextAsync(uiPath, Schema("Changed title"));
                await NextAsync(success.Reader);
                Check(latestTitle == "Changed title" && callbackCount == 2, "UI title update");

                await File.WriteAllTextAsync(luaPath, "value = 2\n");
                await NextAsync(success.Reader);
                Check(callbackCount == 3, "Lua save triggers reload");

                await File.WriteAllTextAsync(uiPath, "{ invalid JSON");
                HotReloadFailedEventArgs jsonFailure = await NextAsync(failure.Reader);
                Check(jsonFailure.Error is SchemaValidationException, "invalid JSON is reported");
                Check(callbackCount == 3, "invalid JSON does not replace UI");
                await File.WriteAllTextAsync(uiPath, Schema("Recovered"));
                await NextAsync(success.Reader);
                Check(latestTitle == "Recovered", "JSON correction recovers");

                await File.WriteAllTextAsync(luaPath, "function broken(\n");
                HotReloadFailedEventArgs luaFailure = await NextAsync(failure.Reader);
                Check(luaFailure.Error is not null, "invalid Lua is reported");
                await File.WriteAllTextAsync(luaPath, "value = 3\n");
                await NextAsync(success.Reader);
                Check((await scheduler.GetPluginSnapshotsAsync()).Single().EntryCode.Source == "value = 3\n", "validated snapshot published for future starts");
                Directory.CreateDirectory(Path.Combine(plugin, "nested"));
                await File.WriteAllTextAsync(Path.Combine(plugin, "nested", "entry.txt"), "entry_value = 1\n");
                string manifestPath = Path.Combine(plugin, "manifest.json");
                await File.WriteAllTextAsync(manifestPath, (await File.ReadAllTextAsync(manifestPath)).Replace("main.lua", "nested/entry.txt"));
                await NextAsync(success.Reader);
                await File.WriteAllTextAsync(Path.Combine(plugin, "nested", "entry.txt"), "entry_value = 2\n");
                await NextAsync(success.Reader);
                Check((await scheduler.GetPluginSnapshotsAsync()).Single().EntryCode.Source == "entry_value = 2\n", "non-.lua entry changes were not monitored");
            }
        }
        finally
        {
            string full = Path.GetFullPath(root);
            string output = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(output, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("hotreload-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a path outside the test output.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static string Schema(string title) =>
        "{\"type\":\"Page\",\"id\":\"root\",\"title\":\"" + title + "\"}";

    private static async Task<T> NextAsync<T>(ChannelReader<T> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Hot reload test failed: " + message);
    }

    private sealed class NoAnomaly : IAnomalyLogger
    {
        public Task<string> LogAnomalyAsync(TaskExecutionContext context, string reason, string detail) => throw new NotSupportedException();
    }
    private sealed class UiUpdate(Action commit) : IPluginUiUpdate
    {
        public Task CommitAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); commit(); return Task.CompletedTask; }
        public Task RollbackAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
