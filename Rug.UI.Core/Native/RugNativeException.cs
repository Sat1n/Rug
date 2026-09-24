namespace Rug.UI.Core.Native;

/// <summary>Thrown when a native Rug.Core call returns a non-zero status code.</summary>
public sealed class RugNativeException : Exception
{
    public string Function { get; }
    public int Status { get; }

    public RugNativeException(string function, int status)
        : base($"Rug.Core native call '{function}' failed (status {status}).")
    {
        Function = function;
        Status = status;
    }
}
