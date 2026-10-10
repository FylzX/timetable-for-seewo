using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Composition;
using System.Runtime.InteropServices;
using ICompositionSupportsSystemBackdrop = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace Timetable.Desktop;

// A clear desktop composition surface lets the panel's own alpha and rounded
// corners show the wallpaper, without applying opacity to its text or buttons.
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private CompositionColorBrush? brush;
    private Compositor? compositor;
    private static nint dispatcherController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        // WinUI owns a Microsoft dispatcher; the Windows compositor also needs
        // its Windows dispatcher on the same UI thread, retained for app lifetime.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
        {
            var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out dispatcherController));
        }
        compositor = new Compositor();
        brush = compositor.CreateColorBrush(Colors.Transparent);
        target.SystemBackdrop = brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        brush?.Dispose();
        brush = null;
        compositor?.Dispose();
        compositor = null;
        base.OnTargetDisconnected(target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
}
