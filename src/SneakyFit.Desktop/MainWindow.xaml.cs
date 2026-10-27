using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SneakyFit.Application;
using SneakyFit.Domain;
using SneakyFit.Desktop.Controls;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow : Window
{
    private readonly WorkspaceSession _session;
    private readonly IAppPaths _paths;
    private string _page = "diary";
    private string _foodFilter = "Любимые", _foodQuery = "", _macroTab = "Ккал";
    private string _measurementKey = "waist";
    private BodySide _measurementSide = BodySide.None;
    private readonly Dictionary<string, int> _chartPeriods = [];
    private bool _threeDays;
    private bool _compactDiary;
    private DateOnly _calendarDay = DateOnly.FromDateTime(DateTime.Now);
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private Func<bool>? _flushDrawer;
    private Action? _releaseDrawer;
    private Action? _refreshProfile;
    private Action? _refreshFoods;
    private string _menuTab = "Прогресс", _progressTab = "Вес";
    private Action<string>? _selectMenuTab;
    private Food? _selectedFood;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<Guid, decimal> _lastPortions = [];
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public MainWindow(WorkspaceSession session, IAppPaths paths)
    {
        InitializeComponent(); _session = session; _paths = paths;
        Icon = Companion.CurrentCatIcon ?? BitmapFrame.Create(new Uri("pack://application:,,,/Assets/SneakyFit.ico"));
        Companion.CatIconChanged += icon => Icon = icon;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 28); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 28); MinWidth = Math.Min(MinWidth, Width); MinHeight = Math.Min(MinHeight, Height);
        _session.Changed += SessionChanged;
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (DrawerOverlay.Visibility == Visibility.Visible) BackDrawer(); else if (_page == "profile") Navigate("diary"); else Companion.Collapse(); e.Handled = true; } if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { Navigate("foods"); e.Handled = true; } };
        SourceInitialized += (_, _) => NativeFrame.Apply(this);
        SizeChanged += (_, _) =>
        {
            ResizeDialog();
            var compact = ActualHeight < 840;
            if (compact == _compactDiary) return;
            _compactDiary = compact;
            if (_page == "diary") Render();
        };
        Closing += (_, e) => { if (_flushDrawer?.Invoke() == false) e.Cancel = true; };
        _calendarTimer.Tick += (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now); if (today == _calendarDay) return;
            if (_flushDrawer?.Invoke() == false) return;
            if (_session.SelectedDate == _calendarDay) _session.SelectedDate = today;
            _calendarDay = today;
            if (_page == "diary") { CloseDrawer(); Render(); } else _refreshProfile?.Invoke();
        };
        _calendarTimer.Start();
        StartAdvisor();
        Closed += (_, _) => { _advisorTimer.Stop(); _advisorLifetime.Cancel(); _calendarTimer.Stop(); _toastTimer.Stop(); _releaseDrawer?.Invoke(); while (_dialogHistory.TryPop(out var frame)) frame.Release?.Invoke(); _session.Changed -= SessionChanged; _updateLifetime.Cancel(); };
        Render();
    }
    private static string N(decimal value, string format = "0") => value.ToString(format, Ru);
    private static string Sign(decimal value) => (value > 0 ? "+" : "") + N(value);
    private static string Outcome(DiaryDay day) => SmartTargets.Outcome(day);
    private static decimal Num(TextBox box, string label, decimal min = 0, decimal max = 10000)
    {
        if (!decimal.TryParse(box.Text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < min || value > max) throw new ArgumentException($"{label}: от {min} до {max}.");
        return value;
    }
    private void Safe(Action action) { try { action(); } catch (Exception error) { if (DrawerOverlay.Visibility == Visibility.Visible) DrawerError.Text = error.Message; else Notify(error.Message); } }
    private void Notify(string message) { ToastText.Text = message; Toast.Visibility = Visibility.Visible; _toastTimer.Stop(); _toastTimer.Start(); }
    private void Navigate(string route)
    {
        if (!CloseDrawer()) return;
        if (route is "foods" or "progress")
        {
            var menuTab = route == "foods" ? "Блюда" : "Прогресс";
            if (_page != "profile") { _menuTab = menuTab; Navigate("profile"); }
            else _selectMenuTab?.Invoke(menuTab);
            return;
        }
        var changedPage = _page != route;
        _page = route; _refreshProfile = null; _refreshFoods = null; _selectMenuTab = null;
        Render(); PageScroll.ScrollToTop();
        if (changedPage) UiMotion.Enter(PageHost, UiMotionKind.Page);
    }
    private void OpenProfile(object sender, RoutedEventArgs e) => Navigate(_page == "diary" ? "profile" : "diary");
    private void SessionChanged() { if (_page == "profile") _refreshProfile?.Invoke(); else Render(); RefreshAdvisor(); }
    private void Render()
    {
        SettingsGlyph.Text = _page == "profile" ? "\uE72B" : "\uE712";
        var label = _page == "profile" ? "Вернуться в дневник" : "Меню";
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, label);
        PageScroll.VerticalScrollBarVisibility = _page == "diary" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        PageHost.Margin = _page == "diary" ? new Thickness(26, 12, 26, 20) : new Thickness(30, 12, 30, 85);
        PageHost.Content = _page == "profile" ? ProfilePage() : DiaryPage();
    }
    private void CloseOverlay(object sender, MouseButtonEventArgs e) { BackDrawer(); e.Handled = true; }
    private void CloseDrawerClick(object sender, RoutedEventArgs e) => BackDrawer();
    private UIElement Tabs(IEnumerable<string> options, string selected, Action<string> change)
    {
        var row = new WrapPanel(); foreach (var option in options) { var button = Btn(option, () => change(option), selected == option ? Purple : "#EBE5F7", selected == option ? "#FFFFFF" : "#655F72"); button.Height = ControlSize; button.Padding = ControlPadding; button.Margin = new Thickness(0, 0, 8, 8); row.Children.Add(button); } return row;
    }
    private string DayName(DateOnly date)
    {
        var delta = date.DayNumber - DateOnly.FromDateTime(DateTime.Now).DayNumber;
        return delta switch { 0 => "Сегодня", 1 => "Завтра", 2 => "Послезавтра", -1 => "Вчера", _ => date.ToString("d MMMM", Ru) };
    }
}
