using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hoardling.Core;

namespace Hoardling.Rendering;

/// <summary>Renders the idle dragon (no rings) to PNG bytes, e.g. for the tray icon.</summary>
internal static class DragonIcon
{
    public static byte[] CreatePng(int size)
    {
        var visual = new PetVisual(PetPalette.Mint, PetPose.Idle, false, false, false, null, null, false, false);
        var renderer = new DragonRenderer();
        renderer.Animate(visual, 0);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            // The dragon occupies 120×120 at offset 25 inside the 170 canvas; crop to it.
            var scale = size / 120.0;
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -25 * scale, -25 * scale));
            renderer.Render(dc, visual, withRings: false);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
