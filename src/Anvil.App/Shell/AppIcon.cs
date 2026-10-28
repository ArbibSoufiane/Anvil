using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Anvil.App.Shell;

/// <summary>The Anvil mark (accent "A" on a dark tile), drawn at runtime for the window and tray.</summary>
internal static class AppIcon
{
    public static WindowIcon Create()
    {
        const int size = 64;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#15100B")), null, new RoundedRect(new Rect(0, 0, size, size), 14));
            var pen = new Pen(new SolidColorBrush(Color.Parse("#F08A3C")), 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            using (ctx.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
                ctx.DrawGeometry(null, pen, StreamGeometry.Parse("M4 20 L12 4 L20 20 M7.5 13.5 H16.5"));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
