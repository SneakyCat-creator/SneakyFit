using System.Windows;
using System.Windows.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private static StackPanel MacroField(string label, TextBox input)
    {
        var editor = new Grid();
        editor.Children.Add(input);
        var zero = T("0", BodySize, "#746B82");
        zero.Margin = ControlPadding;
        zero.IsHitTestVisible = false;
        void Refresh() => zero.Visibility = string.IsNullOrWhiteSpace(input.Text) ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Refresh();
        Refresh();
        editor.Children.Add(zero);
        return Field(label, editor);
    }

    // Only an explicit form submission turns an omitted macronutrient into zero.
    // Missing values received from the catalog remain unknown until that point.
    private static decimal MacroValue(TextBox input, string label) =>
        string.IsNullOrWhiteSpace(input.Text) ? 0 : Num(input, label, 0, 100);
}
