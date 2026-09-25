#nullable enable

namespace Rug.UI.Core.Models;

/// <summary>One committed state change. Values are null when absent or explicitly null.</summary>
public sealed class StateChangedEventArgs(string key, object? previousValue, object? value,
    long revision) : EventArgs
{
    public string Key { get; } = key;
    public object? PreviousValue { get; } = previousValue;
    public object? Value { get; } = value;
    public long Revision { get; } = revision;
}
