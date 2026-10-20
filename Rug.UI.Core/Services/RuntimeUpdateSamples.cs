using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Trusted controlled-window fixture. Installed modifications never inherit node registration.</summary>
public static class RuntimeUpdateSamples
{
    public static LoadedScriptContent Code()
    {
        using var stream = typeof(RuntimeUpdateSamples).Assembly.GetManifestResourceStream("Rug.UpdateProbe.lua")
            ?? throw new InvalidOperationException("Update sample source is missing.");
        using var reader = new StreamReader(stream);
        return CheckpointJson.Loaded("main.lua", reader.ReadToEnd());
    }
    public static CheckpointNode Node(PluginInstallation installation) => new(installation,"1.0.0",Code().ContentHash,
        "panel-ready",1,Array.AsReadOnly(new[]{"panel-ready"}));
}
