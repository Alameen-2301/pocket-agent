using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace PocketAgent.Windows.Tools;

internal sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>Windows' built-in OCR (Windows.Media.Ocr), for apps that expose no accessible controls.</summary>
internal static class OcrReader
{
    public static IReadOnlyList<OcrLine> Read(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                     ?? throw new InvalidOperationException("No OCR language is installed. Add a language with OCR support in Windows Settings → Time & language.");

        // OCR has a maximum image size; scale down large captures and map the boxes back.
        double scale = 1.0;
        Bitmap source = bitmap;
        Bitmap? scaled = null;
        int max = (int)OcrEngine.MaxImageDimension;
        if (bitmap.Width > max || bitmap.Height > max)
        {
            scaled = ScreenCapture.Scale(bitmap, max, out scale);
            source = scaled;
        }
        try
        {
            using var software = ToSoftwareBitmap(source);
            var result = engine.RecognizeAsync(software).AsTask().GetAwaiter().GetResult();
            var lines = new List<OcrLine>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double left = line.Words.Min(w => w.BoundingRect.X), top = line.Words.Min(w => w.BoundingRect.Y);
                double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                lines.Add(new OcrLine(line.Text, Rectangle.FromLTRB(
                    (int)Math.Round(left / scale), (int)Math.Round(top / scale),
                    (int)Math.Round(right / scale), (int)Math.Round(bottom / scale))));
            }
            return lines;
        }
        finally { scaled?.Dispose(); }
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var bgra = bitmap.Clone(rect, PixelFormat.Format32bppArgb);
        var data = bgra.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var buffer = CryptographicBuffer.CreateFromByteArray(bytes);
            return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
        }
        finally { bgra.UnlockBits(data); }
    }
}
