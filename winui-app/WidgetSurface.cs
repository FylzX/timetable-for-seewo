using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Timetable.Desktop;

// ProtectedCursor belongs to the XAML element, so WinUI preserves the shape
// while moving across its children or holding pointer capture during a resize.
public sealed class WidgetSurface : Grid
{
    private InputSystemCursorShape shape = InputSystemCursorShape.Arrow;
    private InputSystemCursor? cursor;
    internal InputSystemCursorShape PointerShape => shape;

    internal void SetPointerShape(InputSystemCursorShape next)
    {
        if (cursor != null && shape == next) return;
        var previous = cursor;
        cursor = InputSystemCursor.Create(next);
        ProtectedCursor = cursor;
        shape = next;
        previous?.Dispose();
    }
}
