using Rug.UI.Core.Models;

namespace Rug.UI.Core.Abstractions;

/// <summary>Validates a physical client pixel and projects it to physical screen space.</summary>
public interface ICoordinateMapper
{
    PointInt ClientToScreen(nint hwnd, int x, int y);
}
