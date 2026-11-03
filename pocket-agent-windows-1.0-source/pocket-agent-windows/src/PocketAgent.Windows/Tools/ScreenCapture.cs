using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace PocketAgent.Windows.Tools;

/// <summary>A clickable element drawn as a numbered box on a "marked" screenshot.</summary>
internal sealed record Mark(int Number, Rectangle Bounds);

internal static class ScreenCapture
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 124, 92, 255), Color.FromArgb(255, 14, 165, 233), Color.FromArgb(255, 16, 185, 129),
        Color.FromArgb(255, 245, 158, 11), Color.FromArgb(255, 236, 72, 153), Color.FromArgb(255, 239, 68, 68),
    ];

    /// <summary>Copies a region of the screen (physical pixels), including layered windows such as menus.</summary>
    public static Bitmap Capture(Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0 || (long)area.Width * area.Height > 40_000_000)
            throw new InvalidOperationException("Display size is unsupported.");
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
        return bmp;
    }

    public static Bitmap Scale(Bitmap source, int maxEdge, out double scale)
    {
        scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var reduced = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(reduced);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, w, h);
        return reduced;
    }

    /// <summary>Draws numbered boxes; bounds are in screen pixels and converted with origin/scale.</summary>
    public static void DrawMarks(Bitmap image, IEnumerable<Mark> marks, Point origin, double scale)
    {
        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float fontSize = (float)Math.Clamp(11 * Math.Max(scale, 0.75), 9, 13);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var mark in marks)
        {
            var color = Palette[mark.Number % Palette.Length];
            var r = new RectangleF(
                (float)((mark.Bounds.Left - origin.X) * scale), (float)((mark.Bounds.Top - origin.Y) * scale),
                (float)(mark.Bounds.Width * scale), (float)(mark.Bounds.Height * scale));
            if (r.Right < 0 || r.Bottom < 0 || r.Left > image.Width || r.Top > image.Height || r.Width < 3 || r.Height < 3) continue;
            using (var pen = new Pen(Color.FromArgb(220, color), 2f)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            var label = mark.Number.ToString();
            var size = g.MeasureString(label, font);
            var tag = new RectangleF(Math.Max(0, r.X - 1), Math.Max(0, r.Y - size.Height + 2), size.Width + 4, size.Height - 2);
            if (tag.Y < 0) tag.Y = r.Y;
            using (var bg = new SolidBrush(color)) g.FillRectangle(bg, tag);
            g.DrawString(label, font, Brushes.White, tag.X + 1, tag.Y - 1);
        }
    }

    public static byte[] EncodeJpeg(Bitmap image, long quality)
    {
        using var stream = new MemoryStream();
        using var settings = new EncoderParameters(1);
        settings.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        image.Save(stream, Jpeg, settings);
        return stream.ToArray();
    }

    /// <summary>Small preview for the activity timeline (never sent to the model).</summary>
    public static string Thumbnail(Bitmap image, int width = 360)
    {
        double scale = Math.Min(1.0, width / (double)image.Width);
        using var thumb = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(image, 0, 0, thumb.Width, thumb.Height);
        }
        return "data:image/jpeg;base64," + Convert.ToBase64String(EncodeJpeg(thumb, 60));
    }
}
