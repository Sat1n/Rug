using System.Text.Json;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>One trusted sample, pinned to embedded shipped Lua rather than mutable installed code.</summary>
public static class CheckpointSamples
{
    public static LoadedScriptContent Code()
    {
        using var stream = typeof(CheckpointSamples).Assembly.GetManifestResourceStream("Rug.CheckpointProbe.lua")
            ?? throw new InvalidOperationException("Checkpoint sample source is missing.");
        using var reader = new StreamReader(stream);
        return CheckpointJson.Loaded("main.lua", reader.ReadToEnd());
    }
    public static CheckpointNode Node(PluginInstallation installation) => new(installation, "1.1.0", Code().ContentHash,
        "panel-ready", 1, Array.AsReadOnly(new[] { "panel-ready" }));
    public sealed class GreenRestoreValidator : ICheckpointRestoreValidator
    {
        public Task<bool> ValidateAsync(CheckpointObservation observation, CheckpointRecord checkpoint, CancellationToken token) =>
            new GreenPanelValidator().ValidateAsync(observation, new(checkpoint.NodeId, checkpoint.State, checkpoint.CompletedActionIds), token);
    }
    public sealed class GreenPanelValidator : ICheckpointValidator
    {
        public async Task<bool> ValidateAsync(CheckpointObservation observation, CheckpointSubmission submission, CancellationToken token)
        {
            var state = submission.State;
            if (state.ValueKind != JsonValueKind.Object || state.EnumerateObject().Count() != 2 ||
                !state.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.String || position.GetString() != "after-panel-ready" ||
                !state.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True) return false;
            var frame = await observation.CaptureFreshAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested();
            int green = 0, total = 0;
            for (int y = frame.Height / 4; y < frame.Height * 3 / 4; y++) for (int x = frame.Width / 4; x < frame.Width * 3 / 4; x++)
            {
                int offset = y * frame.Stride + x * 4; total++; if (frame.Pixels[offset + 1] > 130 && frame.Pixels[offset] < 90 && frame.Pixels[offset + 2] < 90) green++;
            }
            return total > 0 && green > total * 0.7;
        }
    }
}
