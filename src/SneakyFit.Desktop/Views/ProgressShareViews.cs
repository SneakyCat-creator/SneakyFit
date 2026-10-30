using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SneakyFit.Application;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private Button ProgressShareButton(string section)
    {
        var button = Btn("", () => ShareProgressDrawer(section), "#FFFFFF", Ink);
        button.Height = ControlSize;
        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8,6 H 5 C 3.9,6 3,6.9 3,8 V 18 C 3,19.1 3.9,20 5,20 H 17 C 18.1,20 19,19.1 19,18 V 15 M 9,14 C 9,9 13,6 20,6 M 16,2 L 20,6 L 16,10"),
            Width = 18, Height = 18, Stretch = Stretch.Uniform, Stroke = B(Purple), StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Content = H(arrow, M(T("Поделиться", BodySize, Ink, true), left: 8));
        System.Windows.Automation.AutomationProperties.SetName(button, "Поделиться: " + section);
        return button;
    }

    private void ShareProgressDrawer(string section)
    {
        var days = _chartPeriods.GetValueOrDefault(section, 14);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var kind = section switch { "Вес" => ProgressShareKind.Weight, "Параметры" => ProgressShareKind.Measurements,
            "КБЖУ" => ProgressShareKind.Nutrition, _ => ProgressShareKind.Activity };
        var message = ProgressShare.Create(_session.Data, kind, days == 0 ? DateOnly.MinValue : today.AddDays(-days + 1), today, _measurementKey, _measurementSide);
        var preview = Input(message.FullText);
        preview.IsReadOnly = true;
        preview.AcceptsReturn = true;
        preview.TextWrapping = TextWrapping.Wrap;
        preview.VerticalContentAlignment = VerticalAlignment.Top;
        preview.MinHeight = 144; preview.MaxHeight = 244;
        preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        preview.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        System.Windows.Automation.AutomationProperties.SetName(preview, "Текст для публикации");

        void Copy()
        {
            try { Clipboard.SetText(message.FullText); }
            catch (System.Runtime.InteropServices.ExternalException) { DrawerError.Text = "Не удалось скопировать. Попробуйте ещё раз."; return; }
            if (CloseDrawer()) Notify("Скопировано ✓");
        }
        void Open(Uri link)
        {
            try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { DrawerError.Text = "Не удалось открыть. Можно скопировать текст."; return; }
            CloseDrawer();
        }
        Drawer("Поделиться", preview, Equal(Primary("Копировать", Copy), Btn("Telegram", () => Open(message.Telegram)), Btn("WhatsApp", () => Open(message.WhatsApp))), width: 620);
    }
}
