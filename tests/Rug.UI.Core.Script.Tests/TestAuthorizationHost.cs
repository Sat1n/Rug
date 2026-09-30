using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Models;
using Rug.UI.Core.Security;
using Rug.UI.Core.Services;

/// <summary>Explicit trusted test-host approvals. No production automatic approval path exists.</summary>
internal static class TestAuthorizationHost
{
    public static IInputArbiter InputArbiter { get; } = new InputArbiter(new TestInputWindows());
    public static async Task InitializeAsync(LuaRuntime runtime, string script, PluginManifest manifest,
        CancellationToken ct = default)
    {
        manifest = manifest with
        {
            Id = runtime.BoundIdentity?.PluginId ?? (string.IsNullOrEmpty(manifest.Id) ? "test-plugin" : manifest.Id),
            PluginDirectory = string.IsNullOrEmpty(manifest.PluginDirectory) ? Path.GetDirectoryName(Path.GetFullPath(script))! : manifest.PluginDirectory
        };
        runtime.ConfigureInputArbiter(InputArbiter);
        Approve(runtime.Authorization, manifest);
        await runtime.InitializeAsync(script, manifest, ct);
    }
    public static void Approve(IPluginAuthorization authorization, PluginManifest manifest)
    {
        var snapshot = authorization.Query(PluginInstallation.From(manifest));
        if (snapshot.Revision == 0) authorization.Register(manifest);
        authorization.Grant(PluginInstallation.From(manifest), manifest.Permissions);
    }
    public static void ApproveCatalog(IPluginAuthorization authorization, string root)
    {
        foreach (var manifest in new PluginManager(NullLogger<PluginManager>.Instance).ScanPlugins(root))
        {
            authorization.Register(manifest);
            Approve(authorization, manifest);
        }
    }
    public static PermissionInterceptor Gate(PluginManifest manifest, ILogger logger)
    {
        manifest = manifest with { Id = string.IsNullOrEmpty(manifest.Id) ? "test-plugin" : manifest.Id,
            PluginDirectory = string.IsNullOrEmpty(manifest.PluginDirectory) ? AppContext.BaseDirectory : manifest.PluginDirectory };
        var authorization = new PluginAuthorization();
        Approve(authorization, manifest);
        return new(manifest, logger, authorization);
    }
}

internal sealed class TestInputWindows : IInputWindowValidator
{
    public InputTarget Bind(nint hwnd) => hwnd != 0 ? new(hwnd, 1) : throw new ArgumentException("No window");
    public bool IsValid(InputTarget target) => target.Hwnd != 0 && target.WindowStamp == 1;
}
