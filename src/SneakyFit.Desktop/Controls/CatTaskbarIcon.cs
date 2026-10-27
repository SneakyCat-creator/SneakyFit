using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SneakyFit.Desktop.Controls;

internal static class CatTaskbarIcon
{
    private static readonly Dictionary<int, BitmapSource> Cache = [];

    public static BitmapSource ForSprite(int index)
    {
        if (Cache.TryGetValue(index, out var icon)) return icon;
        var sprite = CatSprites.All[index];
        var portrait = Portrait(sprite.Image);
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            // Windows fixes the slot size. Fill that slot with the cat itself,
            // rather than letting floating stars/question marks shrink its face.
            // Every DPI frame is reduced directly from the original portrait.
            var face = DisplayBitmap.Fit(portrait, size, size, new DpiScale(1, 1), sprite.Mirror);
            var facePixels = new byte[face.PixelWidth * face.PixelHeight * 4];
            face.CopyPixels(facePixels, face.PixelWidth * 4, 0);
            var pixels = new byte[size * size * 4];
            var left = (size - face.PixelWidth) / 2;
            var top = (size - face.PixelHeight) / 2;
            for (var row = 0; row < face.PixelHeight; row++)
                Buffer.BlockCopy(facePixels, row * face.PixelWidth * 4, pixels, ((top + row) * size + left) * 4, face.PixelWidth * 4);
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream(); encoder.Save(png); images.Add(png.ToArray());
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            var offset = 6 + sizes.Length * 16;
            for (var i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
            }
            foreach (var png in images) writer.Write(png);
        }
        stream.Position = 0;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // Keep the icon decoder attached so WPF can select distinct small/large frames.
        icon = decoder.Frames[^1]; icon.Freeze(); Cache.Add(index, icon);
        return icon;
    }

    private static BitmapSource Portrait(BitmapSource source)
    {
        var width = source.PixelWidth; var height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, width * 4, 0);
        var visited = new bool[width * height];
        var queue = new int[visited.Length];
        var largest = 0; var bounds = new Int32Rect(0, 0, width, height);
        // The largest connected silhouette is the head (with attached paws or
        // props). Detached decoration should not set a taskbar portrait's scale.
        for (var seed = 0; seed < visited.Length; seed++)
        {
            if (visited[seed] || pixels[seed * 4 + 3] < 32) continue;
            var read = 0; var count = 1; queue[0] = seed; visited[seed] = true;
            var left = seed % width; var right = left; var top = seed / width; var bottom = top;
            while (read < count)
            {
                var position = queue[read++]; var x = position % width; var y = position / width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx; var ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        var neighbour = ny * width + nx;
                        if (visited[neighbour] || pixels[neighbour * 4 + 3] < 32) continue;
                        visited[neighbour] = true; queue[count++] = neighbour;
                    }
            }
            if (count <= largest) continue;
            largest = count;
            var fringe = Math.Max(2, (int)Math.Ceiling(Math.Max(right - left + 1, bottom - top + 1) * .01));
            left = Math.Max(0, left - fringe); top = Math.Max(0, top - fringe);
            right = Math.Min(width - 1, right + fringe); bottom = Math.Min(height - 1, bottom + fringe);
            bounds = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        }
        if (largest == 0) return source;
        var portrait = new CroppedBitmap(source, bounds); portrait.Freeze(); return portrait;
    }
}
