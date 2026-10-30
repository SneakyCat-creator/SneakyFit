using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SneakyFit.Desktop.Controls;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private sealed record DialogFrame(string Title, object? Body, object? Footer, double Width, double? Top,
        Func<bool>? Flush, Action? Release, double Scroll);
    private readonly Stack<DialogFrame> _dialogHistory = new();
    private double _dialogWidth = 560;
    private double? _dialogTop;
    private IInputElement? _beforeDialogFocus;
    private FrameworkElement? _pressedShade;
    private bool _dismissedWithPointer, _consumePointerRelease;

    private void BeginOverlayBack(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DrawerOverlay.Visibility != Visibility.Visible || sender is not FrameworkElement shade) return;
        ResetOverlayPointer();
        // Keep the shade in place for the entire gesture. Closing on Down lets
        // the matching Up activate a diary card beneath the vanished dialog.
        if (shade.CaptureMouse()) _pressedShade = shade;
    }

    private void EndOverlayBack(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var shade = _pressedShade;
        if (shade is null || !ReferenceEquals(sender, shade)) return;
        var insideShade = new Rect(shade.RenderSize).Contains(e.GetPosition(shade));
        var insideDialog = new Rect(DrawerPanel.RenderSize).Contains(e.GetPosition(DrawerPanel));
        ResetOverlayPointer();
        if (!insideShade || insideDialog || DrawerOverlay.Visibility != Visibility.Visible) return;
        if (BackDrawer() && DrawerOverlay.Visibility != Visibility.Visible) _dismissedWithPointer = true;
    }

    private void CancelOverlayBack(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _pressedShade)) _pressedShade = null;
    }

    private void ResetOverlayPointer()
    {
        var shade = _pressedShade;
        _pressedShade = null;
        if (shade?.IsMouseCaptured == true) shade.ReleaseMouseCapture();
        _consumePointerRelease = false;
    }

    private void GuardOverlayClickSeries(object sender, MouseButtonEventArgs e)
    {
        if (!_dismissedWithPointer) return;
        var point = e.GetPosition(this);
        var nativeCaption = point.Y < 37 && point.X >= ActualWidth - 141;
        // A double click used to dismiss a dialog must not spend its second
        // click on the newly exposed Add button. A fresh click works normally.
        if (e.ChangedButton == MouseButton.Left && e.ClickCount > 1 && !nativeCaption)
        {
            e.Handled = true;
            _consumePointerRelease = true;
        }
        else _dismissedWithPointer = false;
    }

    private void FinishOverlayClickSeries(object sender, MouseButtonEventArgs e)
    {
        if (!_consumePointerRelease || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        _consumePointerRelease = false;
    }

    private void SetDialogInteraction(bool open)
    {
        // IsEnabled alone does not exclude raw pointer handlers or the companion,
        // which is drawn above the shade. Keep only the dialog and native chrome interactive.
        foreach (var element in new UIElement[] { PageScroll, SettingsButton, Companion })
        {
            element.IsHitTestVisible = !open;
            element.IsEnabled = !open;
        }
        System.Windows.Controls.Panel.SetZIndex(Companion, open ? 19 : 40);
        if (open)
        {
            KeyboardNavigation.SetTabNavigation(DrawerPanel, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(DrawerPanel, KeyboardNavigationMode.Cycle);
            DrawerPanel.Focusable = true;
            DrawerPanel.Focus();
        }
        else
        {
            ResetOverlayPointer();
            if (_beforeDialogFocus is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous)
                previous.Focus();
            _beforeDialogFocus = null;
        }
    }

    private void ResizeDialog()
    {
        if (DrawerPanel is null) return;
        DrawerPanel.VerticalAlignment = _dialogTop is null ? VerticalAlignment.Center : VerticalAlignment.Top;
        DrawerPanel.Margin = _dialogTop is { } top ? new Thickness(24, top, 24, 24) : new Thickness(24);
        DrawerPanel.Width = Math.Min(_dialogWidth, Math.Max(320, ActualWidth - 80));
        DrawerPanel.MaxHeight = Math.Max(260, ActualHeight - (_dialogTop ?? DrawerPanel.Margin.Top) - DrawerPanel.Margin.Bottom);
        var contentWidth = DrawerPanel.Width - DrawerPanel.Padding.Left - DrawerPanel.Padding.Right;
        DrawerFooter.Visibility = DrawerFooter.Content is null ? Visibility.Collapsed : Visibility.Visible;
        DrawerFooter.Measure(new Size(contentWidth, double.PositiveInfinity));
        var footerHeight = DrawerFooter.Visibility == Visibility.Visible ? DrawerFooter.DesiredSize.Height : 0;
        // The old fixed reserve removed a whole button's worth of space even
        // from dialogs without footer actions (including the weekly balance).
        var chromeHeight = DrawerPanel.Padding.Top + DrawerPanel.Padding.Bottom + 58 + footerHeight;
        DrawerScroll.MaxHeight = Math.Max(160, DrawerPanel.MaxHeight - chromeHeight);
    }

    private void FreezeDialogTop()
    {
        if (_dialogTop is not null || DrawerOverlay.Visibility != Visibility.Visible) return;
        var top = DrawerPanel.TranslatePoint(new Point(0, 0), DrawerOverlay).Y;
        _dialogTop = Math.Max(DrawerPanel.Margin.Top, Math.Round(top));
        ResizeDialog();
    }

    private bool Drawer(string title, UIElement body, UIElement? footer = null, double width = 560, bool replace = false)
    {
        if (_flushDrawer?.Invoke() == false) return false;
        var wasOpen = DrawerOverlay.Visibility == Visibility.Visible;
        if (!wasOpen) _beforeDialogFocus = Keyboard.FocusedElement;
        var nextPage = wasOpen && !replace && DrawerTitle.Text != title;
        if (DrawerOverlay.Visibility == Visibility.Visible)
        {
            if (replace || DrawerTitle.Text == title) _releaseDrawer?.Invoke();
            else _dialogHistory.Push(new(DrawerTitle.Text, DrawerBody.Content, DrawerFooter.Content, _dialogWidth, _dialogTop,
                _flushDrawer, _releaseDrawer, DrawerScroll.VerticalOffset));
        }
        if (!wasOpen || nextPage) _dialogTop = null;
        _flushDrawer = null; _releaseDrawer = null;
        DrawerBody.Content = null; DrawerFooter.Content = null;
        _dialogWidth = width; DrawerTitle.Text = title;
        DrawerBody.Content = body; DrawerFooter.Content = footer; DrawerError.Text = "";
        ResizeDialog();
        DrawerOverlay.Visibility = Visibility.Visible; DrawerScroll.ScrollToTop(); SetDialogInteraction(true);
        Dispatcher.BeginInvoke(FreezeDialogTop, DispatcherPriority.Loaded);
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
            _dialogWidth = previous.Width; _dialogTop = previous.Top; DrawerTitle.Text = previous.Title;
            DrawerBody.Content = previous.Body; DrawerFooter.Content = previous.Footer;
            ResizeDialog();
            _flushDrawer = previous.Flush; _releaseDrawer = previous.Release;
            SetDialogInteraction(true);
            Dispatcher.BeginInvoke(() =>
            {
                DrawerScroll.ScrollToVerticalOffset(previous.Scroll);
                FreezeDialogTop();
            }, DispatcherPriority.Loaded);
            UiMotion.Enter(DrawerPanel, UiMotionKind.Back);
        }
        else
        {
            UiMotion.Stop(DrawerOverlay); UiMotion.Stop(DrawerPanel);
            DrawerOverlay.Visibility = Visibility.Collapsed;
            _dialogTop = null;
            SetDialogInteraction(false);
        }
        return true;
    }

    private bool CloseDrawer()
    {
        if (_flushDrawer?.Invoke() == false) return false;
        _releaseDrawer?.Invoke(); _flushDrawer = null; _releaseDrawer = null;
        while (_dialogHistory.TryPop(out var previous)) previous.Release?.Invoke();
        UiMotion.Stop(DrawerOverlay); UiMotion.Stop(DrawerPanel);
        DrawerOverlay.Visibility = Visibility.Collapsed; DrawerBody.Content = null; DrawerFooter.Content = null;
        _dialogTop = null;
        SetDialogInteraction(false);
        return true;
    }
}