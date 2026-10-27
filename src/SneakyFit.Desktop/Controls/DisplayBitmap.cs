using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SneakyFit.Desktop.Controls;

internal static class DisplayBitmap
{
    // Low-pass Lanczos resampling in premultiplied alpha: transparent edge RGB must
    // not bleed into the visible fur. The result maps 1:1 to physical screen pixels.
    public static BitmapSource Fit(BitmapSource source, double width, double height, DpiScale dpi, bool mirror)
    {
        var scale = Math.Min(width * dpi.DpiScaleX / source.PixelWidth, height * dpi.DpiScaleY / source.PixelHeight);
        var w = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var h = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
        var horizontal = Weights(source.PixelWidth, w); var vertical = Weights(source.PixelHeight, h);
        var intermediate = new float[w * source.PixelHeight * 4];
        for (var y = 0; y < source.PixelHeight; y++)
            for (var x = 0; x < w; x++)
                foreach (var (index, weight) in horizontal[x])
                    for (var channel = 0; channel < 4; channel++)
                        intermediate[(y * w + x) * 4 + channel] += pixels[(y * source.PixelWidth + index) * 4 + channel] * weight;
        var output = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var offset = (y * w + (mirror ? w - 1 - x : x)) * 4;
                for (var channel = 3; channel >= 0; channel--)
                {
                    double value = 0;
                    foreach (var (index, weight) in vertical[y]) value += intermediate[(index * w + x) * 4 + channel] * weight;
                    output[offset + channel] = (byte)Math.Clamp((int)Math.Round(value), 0, channel == 3 ? 255 : output[offset + 3]);
                }
            }
        var result = BitmapSource.Create(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null, output, w * 4);
        result.Freeze(); return result;
    }
    private static (int Index, float Weight)[][] Weights(int from, int to)
    {
        var ratio = (double)from / to; var filter = Math.Max(1, ratio); var support = 3 * filter;
        var result = new (int, float)[to][];
        for (var target = 0; target < to; target++)
        {
            var center = (target + .5) * ratio - .5;
            var weights = new List<(int Index, double Weight)>(); double sum = 0;
            for (var source = Math.Max(0, (int)Math.Ceiling(center - support)); source <= Math.Min(from - 1, (int)Math.Floor(center + support)); source++)
            {
                var distance = Math.Abs(source - center) / filter;
                var weight = distance < 1e-8 ? 1 : distance >= 3 ? 0 : Math.Sin(Math.PI * distance) * Math.Sin(Math.PI * distance / 3) / (Math.PI * Math.PI * distance * distance / 3);
                weights.Add((source, weight)); sum += weight;
            }
            result[target] = weights.Select(v => (v.Index, (float)(v.Weight / sum))).ToArray();
        }
        return result;
    }
}
