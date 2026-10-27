using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SneakyFit.Desktop.Controls;

internal sealed record CatSprite(BitmapSource Image, bool Mirror, CatMood Mood);

internal static class CatSprites
{
    public static IReadOnlyList<CatSprite> All { get; } = Load();

    private readonly record struct Region(int Left, int Top, int Right, int Bottom, CatMood Mood, bool Mirror = false);

    private static IReadOnlyList<CatSprite> Load()
    {
        // Atlas cells are deliberately not equal: paws and punctuation extend into the gutters.
        Region[] first =
        [
            new(34, 12, 340, 274, CatMood.Thinking, true),
            new(344, 18, 674, 268, CatMood.Celebration, true),
            new(674, 12, 948, 268, CatMood.Surprise),
            new(970, 32, 1255, 274, CatMood.Idle, true),
            new(1255, 44, 1536, 279, CatMood.Celebration),

            new(12, 286, 355, 536, CatMood.Celebration, true),
            new(360, 286, 657, 538, CatMood.Idle, true),
            new(655, 282, 926, 536, CatMood.Thinking),
            new(938, 308, 1244, 545, CatMood.Idle, true),
            new(1245, 299, 1536, 539, CatMood.Idle),

            new(14, 546, 329, 782, CatMood.Surprise, true),
            new(334, 551, 626, 777, CatMood.Idle),
            new(631, 551, 922, 780, CatMood.Celebration, true),
            new(925, 613, 1225, 782, CatMood.Thinking),
            new(1227, 549, 1520, 770, CatMood.Celebration),

            new(53, 794, 337, 1010, CatMood.Idle, true),
            new(340, 780, 635, 1008, CatMood.Idle),
            new(647, 785, 913, 1004, CatMood.Surprise),
            new(924, 784, 1196, 1007, CatMood.Idle),
            new(1211, 831, 1500, 1014, CatMood.Thinking)
        ];
        Region[] second =
        [
            new(3, 12, 276, 264, CatMood.Thinking, true),
            new(294, 20, 607, 258, CatMood.Celebration, true),
            new(632, 15, 936, 260, CatMood.Surprise),
            new(948, 14, 1248, 266, CatMood.Celebration),
            new(1269, 17, 1526, 266, CatMood.Idle),

            new(10, 276, 294, 511, CatMood.Idle, true),
            new(310, 267, 598, 514, CatMood.Celebration),
            new(619, 362, 919, 513, CatMood.Thinking),
            new(935, 310, 1244, 509, CatMood.Idle),
            new(1256, 271, 1536, 514, CatMood.Idle),

            new(1, 524, 287, 768, CatMood.Idle, true),
            new(308, 523, 613, 765, CatMood.Idle),
            new(642, 526, 923, 760, CatMood.Celebration, true),
            new(955, 520, 1243, 766, CatMood.Celebration, true),
            new(1308, 510, 1526, 767, CatMood.Thinking, true),

            new(1, 824, 331, 988, CatMood.Idle, true),
            new(337, 766, 644, 1003, CatMood.Surprise),
            new(646, 761, 937, 998, CatMood.Idle, true),
            new(940, 766, 1264, 1007, CatMood.Idle),
            new(1264, 777, 1536, 1006, CatMood.Celebration)
        ];
        return Slice("cat-emotions-a.png", first).Concat(Slice("cat-emotions-b.png", second)).ToArray();
    }

    private static IEnumerable<CatSprite> Slice(string file, IEnumerable<Region> regions)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri($"pack://application:,,,/Assets/{file}");
        image.EndInit(); image.Freeze();
        foreach (var region in regions)
        {
            var cell = new CroppedBitmap(image, new Int32Rect(region.Left, region.Top, region.Right - region.Left, region.Bottom - region.Top));
            cell.Freeze();
            yield return new(TrimTransparentMargin(cell), region.Mirror, region.Mood);
        }
    }

    private static BitmapSource TrimTransparentMargin(BitmapSource source)
    {
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
        var left = source.PixelWidth; var right = -1; var top = source.PixelHeight; var bottom = -1;
        for (var y = 0; y < source.PixelHeight; y++)
            for (var x = 0; x < source.PixelWidth; x++)
                if (pixels[(y * source.PixelWidth + x) * 4 + 3] > 0)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        if (right < left) return source;
        left = Math.Max(0, left - 1); top = Math.Max(0, top - 1);
        right = Math.Min(source.PixelWidth - 1, right + 1); bottom = Math.Min(source.PixelHeight - 1, bottom + 1);
        var result = new CroppedBitmap(source, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
        result.Freeze(); return result;
    }
}
