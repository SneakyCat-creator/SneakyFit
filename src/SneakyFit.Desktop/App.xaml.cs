using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SneakyFit.Application;
using SneakyFit.Desktop.Controls;
using SneakyFit.Infrastructure;

namespace SneakyFit.Desktop;

public partial class App : System.Windows.Application
{
    private AppInstance? _instance;
    public App()
    {
        // No hover pop-ups anywhere, including controls added in future screens.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), System.Windows.Controls.ToolTipService.ToolTipOpeningEvent,
            new System.Windows.Controls.ToolTipEventHandler((_, e) => e.Handled = true));
    }

    private void PopupOpened(object sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement content } popup)
            UiMotion.Enter(content, popup.TemplatedParent is DatePicker ? UiMotionKind.Calendar : UiMotionKind.Menu);
    }

    private void PopupClosed(object sender, EventArgs e)
    {
        if (sender is Popup { Child: FrameworkElement content }) UiMotion.Stop(content);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UpdateBootstrap.TryHandle(e.Args)) { Shutdown(); return; }
        var paths = new PortablePaths();
        try
        {
            _instance = new AppInstance();
            if (!_instance.Acquire()) { Shutdown(); return; }
            var repository = new JsonWorkspaceRepository(paths);
            var data = repository.Load();
            if (data is null) { data = WorkspaceFactory.Create(); repository.Save(data); }
            MainWindow = new MainWindow(new WorkspaceSession(repository, data), paths);
            _instance.Listen(MainWindow);
            UpdateBootstrap.ObserveStartup(MainWindow, e.Args);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show("Не удалось открыть профиль.\n" + error.Message, "SneakyFit", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
