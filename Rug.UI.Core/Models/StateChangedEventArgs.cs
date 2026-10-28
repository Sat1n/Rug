#nullable enable

namespace Rug.UI.Core.Models;

/// <summary>One committed scoped change with detached JSON values.</summary>
public sealed class StateChangedEventArgs(StateScope scope, string key, object? previousValue, object? value,
    long revision, bool hadPreviousValue) : EventArgs
{
    public StateScope Scope { get; } = scope;
    public string Key { get; } = key;
    public object? PreviousValue { get; } = previousValue;
    public object? Value { get; } = value;
    public long Revision { get; } = revision;
    public bool HadPreviousValue { get; } = hadPreviousValue;
}
