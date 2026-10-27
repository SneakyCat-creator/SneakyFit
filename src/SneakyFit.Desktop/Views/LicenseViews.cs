using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private void LicensesDrawer()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("SneakyFit.Licenses.ThirdParty.txt");
        if (stream is null) { Notify("Не удалось открыть лицензии."); return; }
        using var reader = new StreamReader(stream);
        var notices = new TextBox
        {
            Text = reader.ReadToEnd(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = CaptionSize,
            Foreground = B(Ink),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Height = Math.Clamp(ActualHeight - 280, 180, 420),
            Style = null
        };
        Drawer("Лицензии", notices, width: 680);
    }
}
