#nullable enable
using Rug.UI.Core.Abstractions;
using Rug.UI.Core.Exceptions;
using Rug.UI.Core.Models;

namespace Rug.UI.Core.Services;

/// <summary>Process-local grants, entirely independent of StateStore. Exact names only.</summary>
public sealed class PluginAuthorization : IPluginAuthorization
{
    private sealed class Entry
    {
        public HashSet<string> Requested = new(StringComparer.Ordinal);
        public HashSet<string> Granted = new(StringComparer.Ordinal);
        public Dictionary<string, long> Epochs = new(StringComparer.Ordinal);
        public HashSet<AuthorizationLease> Leases = [];
        public long Revision;
    }
    private readonly Func<PluginInstallation, string, bool> _policy;
    public PluginAuthorization(Func<PluginInstallation, string, bool>? policy = null) =>
        _policy = policy ?? ((_, _) => true);
    private readonly object _gate = new();
    private readonly Dictionary<PluginInstallation, Entry> _entries = [];
    public event EventHandler<AuthorizationChangedEventArgs>? Changed;
    public int ActiveLeaseCount { get { lock (_gate) return _entries.Values.Sum(e => e.Leases.Count); } }
    private AuthorizationSnapshot Snapshot(PluginInstallation id, Entry e) => new(id, e.Revision,
        Array.AsReadOnly(e.Requested.Order(StringComparer.Ordinal).ToArray()),
        Array.AsReadOnly(e.Granted.Intersect(e.Requested).Order(StringComparer.Ordinal).ToArray()),
        Array.AsReadOnly(e.Granted.Intersect(e.Requested).Where(p => Permission.IsKnown(p) && _policy(id, p)).Order(StringComparer.Ordinal).ToArray()));
    /// <summary>@shape manifest: host-verified catalog declaration; return: detached requested/granted/effective snapshot.
    /// @source manifest: Services/PluginManager.cs#function:ScanPlugins</summary>
    public AuthorizationSnapshot Register(PluginManifest manifest)
    {
        var id = PluginInstallation.From(manifest);
        AuthorizationSnapshot snapshot;
        AuthorizationLease[] cancel = [];
        bool changed;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) _entries.Add(id, e = new());
            var requested = new HashSet<string>(manifest.Permissions, StringComparer.Ordinal);
            changed = !e.Requested.SetEquals(requested);
            var removed = e.Requested.Except(requested).ToArray();
            foreach (var p in removed) { e.Granted.Remove(p); e.Epochs[p] = e.Epochs.GetValueOrDefault(p) + 1; }
            cancel = e.Leases.Where(l => l.Permissions.Intersect(removed).Any()).ToArray();
            e.Requested = requested;
            if (changed) e.Revision++;
            snapshot = Snapshot(id, e);
        }
        foreach (var lease in cancel) lease.Cancel();
        if (changed) Notify(snapshot);
        return snapshot;
    }
    public AuthorizationSnapshot Query(PluginInstallation installation)
    {
        lock (_gate) return _entries.TryGetValue(installation, out var e)
            ? Snapshot(installation, e) : new(installation, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    }
    public AuthorizationSnapshot Grant(PluginInstallation id, IEnumerable<string> permissions) => Change(id, permissions, true);
    public AuthorizationSnapshot Revoke(PluginInstallation id, IEnumerable<string> permissions) => Change(id, permissions, false);
    /// <summary>@shape permissions: exact declared names; cancellation: collected leases, after atomic grant update.
    /// @source grants: IPluginAuthorization trusted host management contract</summary>
    private AuthorizationSnapshot Change(PluginInstallation id, IEnumerable<string> permissions, bool grant)
    {
        var names = permissions.Distinct(StringComparer.Ordinal).ToArray();
        AuthorizationSnapshot snapshot;
        AuthorizationLease[] cancel;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e)) throw new ArgumentException("Installation is not registered.");
            if (names.Any(p => !Permission.IsKnown(p) || !e.Requested.Contains(p)))
                throw new ArgumentException("Permission is unknown or not requested.");
            var changed = names.Where(p => grant ? e.Granted.Add(p) : e.Granted.Remove(p)).ToArray();
            if (!grant) foreach (var p in changed) e.Epochs[p] = e.Epochs.GetValueOrDefault(p) + 1;
            cancel = grant ? [] : e.Leases.Where(l => l.Permissions.Intersect(changed).Any()).ToArray();
            if (changed.Length > 0) e.Revision++;
            snapshot = Snapshot(id, e);
        }
        // Grants are already invalid. Cancellation callbacks and host UI execute outside this lock.
        foreach (var lease in cancel) lease.Cancel();
        Notify(snapshot);
        return snapshot;
    }
    public AuthorizationTicket Bind(PluginManifest manifest, IEnumerable<string>? capabilities = null)
    {
        var id = PluginInstallation.From(manifest);
        bool exists;
        lock (_gate) exists = _entries.ContainsKey(id);
        if (!exists) Register(manifest);
        lock (_gate)
        {
            var e = _entries[id];
            var caps = (capabilities ?? manifest.Permissions).Intersect(manifest.Permissions, StringComparer.Ordinal).ToArray();
            return new(this, id, caps, caps.ToDictionary(p => p, p => e.Epochs.GetValueOrDefault(p), StringComparer.Ordinal));
        }
    }
    public IReadOnlyList<string> Missing(AuthorizationTicket ticket, IEnumerable<string> required)
    { lock (_gate) return Array.AsReadOnly(MissingCore(ticket, required)); }
    private string[] MissingCore(AuthorizationTicket t, IEnumerable<string> required) => required.Distinct(StringComparer.Ordinal)
        .Where(p => !ReferenceEquals(t.Issuer, this) || !_entries.TryGetValue(t.Installation, out var e) ||
            !Permission.IsKnown(p) || !_policy(t.Installation, p) || !t.Capabilities.Contains(p) || !e.Requested.Contains(p) || !e.Granted.Contains(p) ||
            !t.Epochs.TryGetValue(p, out long epoch) || epoch != e.Epochs.GetValueOrDefault(p)).ToArray();
    /// <summary>@shape ticket: issuer/install/revocation-stamped ceiling; required: exact names; return: disposable cancellation lease.
    /// @source ticket: PluginAuthorization.Bind</summary>
    public AuthorizationLease Admit(AuthorizationTicket ticket, IEnumerable<string> required)
    {
        var names = required.Distinct(StringComparer.Ordinal).ToArray();
        lock (_gate)
        {
            if (!ReferenceEquals(ticket.Issuer, this) || !_entries.ContainsKey(ticket.Installation))
                throw new ArgumentException("Authorization ticket issuer or installation is invalid.", nameof(ticket));
            var missing = MissingCore(ticket, names);
            if (missing.Length > 0) throw new PermissionDeniedException(ticket.Installation.PluginId, missing[0], "authorization admission", missing);
            var e = _entries[ticket.Installation];
            var lease = new AuthorizationLease(names, l => { lock (_gate) e.Leases.Remove(l); });
            e.Leases.Add(lease);
            return lease;
        }
    }
    private void Notify(AuthorizationSnapshot snapshot)
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler<AuthorizationChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(snapshot)); }
            catch { /* A faulty observer cannot prevent revocation and remaining observers. */ }
        }
    }
}
