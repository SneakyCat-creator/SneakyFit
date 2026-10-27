using System.IO;
using System.Windows;
using System.Windows.Controls;
using SneakyFit.Updates;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _updateAttempt;
    private bool _updating;

    private UIElement UpdateSettings()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var status = T("", BodySize, Muted);
        var update = Primary(_updating ? "Обновление…" : "Обновить", () => { }); update.IsEnabled = !_updating;
        var cancel = Btn("Отмена", () => _updateAttempt?.Cancel()); cancel.Visibility = _updating ? Visibility.Visible : Visibility.Collapsed;
        update.Click += async (_, _) => await UpdateNow(update, cancel, status);
        var actions = H(M(cancel, right: 8), update);
        var statusRow = M(status, top: 12);
        var style = new Style(typeof(Border));
        var empty = new DataTrigger { Binding = new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = status }, Value = "" };
        empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed)); style.Triggers.Add(empty); statusRow.Style = style;
        return S(Between(T("Версия " + version, BodySize, Muted), actions), statusRow,
            M(Btn("Лицензии", LicensesDrawer, "#00FFFFFF", Muted), top: 6));
    }

    private async Task UpdateNow(Button button, Button cancel, TextBlock status)
    {
        if (_updating) return;
        _updating = true; button.IsEnabled = false; cancel.Visibility = Visibility.Visible;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token); _updateAttempt = attempt;
        try
        {
            status.Foreground = B(Muted); status.Text = "Проверяю обновление…";
            var found = await GitHubUpdates.Find(attempt.Token);
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
            if (found is null || found.Version <= current) { status.Text = "Установлена последняя версия ✓"; return; }
            status.Text = "Загрузка…";
            await Task.Run(UpdateHandoff.CleanOldDownloads, attempt.Token);
            var downloaded = await GitHubUpdates.Download(found, new Progress<int>(percent => status.Text = "Загрузка · " + percent + "%"), attempt.Token);
            if (!CloseDrawer()) { status.Text = "Сохраните изменения перед обновлением."; UpdateHandoff.RemoveDirectory(downloaded.Directory); return; }
            status.Text = "Подготовка к перезапуску…";
            await UpdateHandoff.Prepare(downloaded, attempt.Token);
            cancel.IsEnabled = false;
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { status.Text = "Обновление отменено"; }
        catch (Exception error) { status.Text = error is IOException ? error.Message : "Не удалось обновить приложение. Повторите позже."; status.Foreground = B(Coral); }
        finally
        {
            _updateAttempt = null; _updating = false; button.IsEnabled = true; button.Content = "Обновить";
            cancel.Visibility = Visibility.Collapsed; cancel.IsEnabled = true;
        }
    }
}
