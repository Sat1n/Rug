#nullable enable
namespace Rug.UI.Core.Models;

/// <summary>Catalog verified identity; Windows installation paths compare case insensitively.</summary>
public sealed record PluginInstallation
{
    public PluginInstallation(string pluginId, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        PluginId = pluginId.ToUpperInvariant();
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
    }
    public string PluginId { get; }
    public string Directory { get; }
    public static PluginInstallation From(PluginManifest manifest) => new(manifest.Id, manifest.PluginDirectory);
}

public sealed record AuthorizationSnapshot(PluginInstallation Installation, long Revision,
    IReadOnlyList<string> Requested, IReadOnlyList<string> Granted, IReadOnlyList<string> Effective);
public sealed class AuthorizationChangedEventArgs(AuthorizationSnapshot snapshot) : EventArgs
{
    public AuthorizationSnapshot Snapshot { get; } = snapshot;
}

/// <summary>Host-issued capability upper bound and revocation stamps, never a grant.</summary>
public sealed class AuthorizationTicket
{
    /// <summary>For trusted IPluginAuthorization implementations; never deserialize a request into a ticket.</summary>
    public AuthorizationTicket(object issuer, PluginInstallation installation, string[] capabilities,
        IReadOnlyDictionary<string, long> epochs)
    { Issuer = issuer; Installation = installation; Capabilities = Array.AsReadOnly(capabilities.ToArray());
        Epochs = new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new Dictionary<string, long>(epochs)); }
    internal object Issuer { get; }
    internal IReadOnlyDictionary<string, long> Epochs { get; }
    public PluginInstallation Installation { get; }
    public IReadOnlyList<string> Capabilities { get; }
}

/// <summary>Admission occurs under the authorization lock; handlers run after it is released.
/// Revocation cancels this lease irrevocably. Dispose removes its registration.</summary>
public sealed class AuthorizationLease : IDisposable
{
    private readonly Action<AuthorizationLease> _release;
    private readonly CancellationTokenSource _cancellation = new();
    private int _disposed;
    /// <summary>For trusted authorization implementations; disposal unregisters this lease.</summary>
    public AuthorizationLease(string[] permissions, Action<AuthorizationLease> release)
    { Permissions = permissions.ToArray(); _release = release; Token = _cancellation.Token; }
    internal string[] Permissions { get; }
    public void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* All callbacks have been attempted; one observer cannot prevent other leases cancelling. */ }
    }
    public CancellationToken Token { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release(this);
        _cancellation.Dispose();
    }
}
