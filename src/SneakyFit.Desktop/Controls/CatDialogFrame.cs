using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SneakyFit.Desktop.Controls;

// The lower-left frame from the supplied atlas. Fixed corners keep its decorations
// intact while only the quiet interior and straight border sections stretch.
internal sealed class CatDialogFrame : FrameworkElement
{
    public static Thickness ContentInsets { get; } = new(38, 58, 60, 64);
    public static Thickness CompactContentInsets { get; } = new(32, 36, 62, 36);
    public static Thickness CloseInsets { get; } = new(0, 31, 24, 0);
    private static readonly BitmapSource[,] Pieces = Load();
    public CatDialogFrame()
    {
        IsHitTestVisible = false; UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }
    private static BitmapSource[,] Load()
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri("pack://application:,,,/Assets/cat-dialog-frames.png");
        source.EndInit(); source.Freeze();
        // Each cut falls outside the ears, paw prints, dumbbell and tail.
        // The atlas's adjacent three frames never enter the sampled rectangle.
        int[] xs = [8, 185, 690, 880], ys = [414, 579, 650, 848];
        var pieces = new BitmapSource[3, 3];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                var piece = new CroppedBitmap(source, new Int32Rect(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]));
                piece.Freeze(); pieces[row, col] = piece;
            }
        return pieces;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / 872, Math.Min(.5, ActualHeight / 363));
        double[] xs = [0, 177 * scale, ActualWidth - 190 * scale, ActualWidth];
        double[] ys = [0, 165 * scale, ActualHeight - 198 * scale, ActualHeight];
        var dpi = VisualTreeHelper.GetDpi(this);
        for (var i = 0; i < 4; i++)
        {
            xs[i] = Math.Round(xs[i] * dpi.DpiScaleX) / dpi.DpiScaleX;
            ys[i] = Math.Round(ys[i] * dpi.DpiScaleY) / dpi.DpiScaleY;
        }
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                dc.DrawImage(Pieces[row, col], new Rect(xs[col], ys[row], Math.Max(0, xs[col + 1] - xs[col]), Math.Max(0, ys[row + 1] - ys[row])));
    }
}
