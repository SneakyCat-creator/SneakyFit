using System.Windows;
using System.Windows.Threading;
using SneakyFit.Desktop.Controls;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private sealed record DialogFrame(string Title, object? Body, object? Footer, double Width,
        Func<bool>? Flush, Action? Release, double Scroll);
    private readonly Stack<DialogFrame> _dialogHistory = new();
    private double _dialogWidth = 560;

    private void ResizeDialog()
    {
        if (DrawerPanel is null) return;
        var top = Math.Clamp(Math.Round(ActualHeight * .085), 60, 84);
        DrawerPanel.Margin = new Thickness(24, top, 24, 24);
        DrawerPanel.Width = Math.Min(_dialogWidth, Math.Max(320, ActualWidth - 80));
        DrawerPanel.MaxHeight = Math.Min(700, Math.Max(260, ActualHeight - top - 32));
        var contentWidth = DrawerPanel.Width - DrawerPanel.Padding.Left - DrawerPanel.Padding.Right;
        DrawerFooter.Visibility = DrawerFooter.Content is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerFooter.Measure(new Size(contentWidth, double.PositiveInfinity));
        var footerHeight = DrawerFooter.Visibility == Visibility.Visible ? DrawerFooter.DesiredSize.Height : 0;
        // The old fixed reserve removed a whole button's worth of space even
        // from dialogs without footer actions (including the weekly balance).
        var chromeHeight = DrawerPanel.Padding.Top + DrawerPanel.Padding.Bottom + 58 + footerHeight;
        DrawerScroll.MaxHeight = Math.Max(160, DrawerPanel.MaxHeight - chromeHeight);
    }
    private bool Drawer(string title, UIElement body, UIElement? footer = null, double width = 560, bool replace = false)
    {
        if (_flushDrawer?.Invoke() == false) return false;
        var wasOpen = DrawerOverlay.Visibility == Visibility.Visible;
        var nextPage = wasOpen && !replace && DrawerTitle.Text != title;
        if (DrawerOverlay.Visibility == Visibility.Visible)
        {
            if (replace || DrawerTitle.Text == title) _releaseDrawer?.Invoke();
            else _dialogHistory.Push(new(DrawerTitle.Text, DrawerBody.Content, DrawerFooter.Content, _dialogWidth,
                _flushDrawer, _releaseDrawer, DrawerScroll.VerticalOffset));
        }
        _flushDrawer = null; _releaseDrawer = null;
        DrawerBody.Content = null; DrawerFooter.Content = null;
        _dialogWidth = width; DrawerTitle.Text = title;
        DrawerBody.Content = body; DrawerFooter.Content = footer; DrawerError.Text = "";
        ResizeDialog();
        DrawerOverlay.Visibility = Visibility.Visible; DrawerScroll.ScrollToTop(); PageScroll.IsEnabled = false; SettingsButton.IsEnabled = false;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(DrawerPanel, System.Windows.Input.KeyboardNavigationMode.Cycle);
        DrawerPanel.Focusable = true; DrawerPanel.Focus();
        if (!wasOpen)
        {
            UiMotion.Enter(DrawerOverlay, UiMotionKind.Shade);
            UiMotion.Enter(DrawerPanel, UiMotionKind.Dialog);
        }
        else if (nextPage) UiMotion.Enter(DrawerPanel, UiMotionKind.Forward);
        return true;
    }
    private bool BackDrawer()
    {
        if (_flushDrawer?.Invoke() == false) return false;
        _releaseDrawer?.Invoke(); _flushDrawer = null; _releaseDrawer = null;
        DrawerBody.Content = null; DrawerFooter.Content = null; DrawerError.Text = "";
        if (_dialogHistory.TryPop(out var previous))
        {
            _dialogWidth = previous.Width; DrawerTitle.Text = previous.Title;
            DrawerBody.Content = previous.Body; DrawerFooter.Content = previous.Footer;
            ResizeDialog();
            _flushDrawer = previous.Flush; _releaseDrawer = previous.Release;
            Dispatcher.BeginInvoke(() => DrawerScroll.ScrollToVerticalOffset(previous.Scroll), DispatcherPriority.Loaded);
            UiMotion.Enter(DrawerPanel, UiMotionKind.Back);
        }
        else { UiMotion.Stop(DrawerOverlay); UiMotion.Stop(DrawerPanel); DrawerOverlay.Visibility = Visibility.Collapsed; PageScroll.IsEnabled = true; SettingsButton.IsEnabled = true; }
        return true;
    }
    private bool CloseDrawer()
    {
        if (_flushDrawer?.Invoke() == false) return false;
        _releaseDrawer?.Invoke(); _flushDrawer = null; _releaseDrawer = null;
        while (_dialogHistory.TryPop(out var previous)) previous.Release?.Invoke();
        UiMotion.Stop(DrawerOverlay); UiMotion.Stop(DrawerPanel);
        DrawerOverlay.Visibility = Visibility.Collapsed; DrawerBody.Content = null; DrawerFooter.Content = null;
        PageScroll.IsEnabled = true; SettingsButton.IsEnabled = true;
        return true;
    }
}
