using System.IO;
using System.Windows;
using System.Windows.Controls;
using SneakyFit.Desktop.Controls;
using SneakyFit.Updates;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private static readonly Version InstalledVersion = typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _updateAttempt;
    private GitHubRelease? _offeredUpdate;
    private Action? _refreshUpdateSettings;

    private static string VersionLabel(Version version) => version.Revision > 0 ? version.ToString() : version.ToString(3);

    internal async Task CheckForUpdateOnStartup()
    {
        GitHubRelease? release;
        try { release = await GitHubUpdates.Find(_updateLifetime.Token); }
        catch (Exception) { return; } // The manual check reports connection and release errors.
        if (_updateLifetime.IsCancellationRequested || _updateAttempt is not null || release is null || release.Version <= InstalledVersion ||
            _session.Data.Profile.DeclinedUpdateVersion == VersionLabel(release.Version)) return;
        ShowUpdateOffer(release);
    }

    private void ShowUpdateOffer(GitHubRelease release)
    {
        var entering = UpdateBanner.Visibility != Visibility.Visible;
        _offeredUpdate = release;
        UpdateTitle.Text = "Доступно обновление " + VersionLabel(release.Version);
        UpdateNotes.Text = release.Description;
        UpdateNotesScroll.Visibility = release.Description.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdatePageScrolling();
        if (entering) UiMotion.Enter(UpdateBanner, UiMotionKind.Menu);
    }

    private void HideUpdateOffer()
    {
        _offeredUpdate = null;
        UpdateBanner.Visibility = Visibility.Collapsed;
        UpdatePageScrolling();
    }

    private async void AcceptUpdate(object sender, RoutedEventArgs e)
    {
        if (_offeredUpdate is { } release) await UpdateNow(release);
    }

    private void DeclineOrCancelUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateAttempt is { } attempt) { attempt.Cancel(); return; }
        if (_offeredUpdate is not { } release) return;
        Safe(() =>
        {
            _session.SaveProfile(_session.Data.Profile with { DeclinedUpdateVersion = VersionLabel(release.Version) });
            HideUpdateOffer();
            SetUpdateStatus("");
        });
    }

    private UIElement UpdateSettings()
    {
        var status = T("", BodySize, Muted);
        var update = Primary("Обновить", () => { });
        var cancel = Btn("Отмена", () => _updateAttempt?.Cancel());
        update.Click += async (_, _) => await UpdateNow();
        var actions = H(M(cancel, right: 8), update);
        var statusRow = M(status, top: 12);
        _refreshUpdateSettings = () =>
        {
            update.IsEnabled = _updateAttempt is null; update.Content = UpdateAccept.Content;
            cancel.Visibility = _updateAttempt is null ? Visibility.Collapsed : Visibility.Visible;
            status.Text = UpdateStatus.Text; status.Foreground = UpdateStatus.Foreground; statusRow.Visibility = UpdateStatus.Visibility;
        };
        _refreshUpdateSettings();
        return S(Between(T("Версия " + VersionLabel(InstalledVersion), BodySize, Muted), actions), statusRow,
            M(Btn("Лицензии", LicensesDrawer, "#00FFFFFF", Muted), top: 6));
    }

    private void SetUpdateStatus(string message, bool error = false)
    {
        UpdateStatus.Text = message;
        UpdateStatus.Foreground = B(error ? "#AC3448" : "#65546F");
        UpdateStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _refreshUpdateSettings?.Invoke();
    }

    private void RefreshUpdateActions()
    {
        var busy = _updateAttempt is not null;
        UpdateAccept.IsEnabled = !busy;
        UpdateAccept.Content = busy ? "Обновление…" : "Обновить";
        UpdateSecondary.Content = busy ? "Отмена" : "Отклонить";
        _refreshUpdateSettings?.Invoke();
    }

    private async Task UpdateNow(GitHubRelease? offered = null)
    {
        if (_updateAttempt is not null || _updateLifetime.IsCancellationRequested) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token); _updateAttempt = attempt;
        RefreshUpdateActions();
        try
        {
            if (offered is null) SetUpdateStatus("Проверяю обновление…");
            var found = offered ?? await GitHubUpdates.Find(attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            if (found is null || found.Version <= InstalledVersion) { HideUpdateOffer(); SetUpdateStatus("Установлена последняя версия ✓"); return; }
            ShowUpdateOffer(found);
            SetUpdateStatus("Загрузка…");
            await Task.Run(UpdateHandoff.CleanOldDownloads, attempt.Token);
            var downloading = true;
            var progress = new Progress<int>(percent =>
            {
                if (downloading && _updateAttempt == attempt && !attempt.IsCancellationRequested) SetUpdateStatus($"Загрузка · {percent}%");
            });
            var downloaded = await GitHubUpdates.Download(found, progress, attempt.Token);
            downloading = false;
            if (!CloseDrawer()) { SetUpdateStatus("Сохраните изменения перед обновлением."); UpdateHandoff.RemoveDirectory(downloaded.Directory); return; }
            SetUpdateStatus("Подготовка к перезапуску…");
            await UpdateHandoff.Prepare(downloaded, attempt.Token);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { SetUpdateStatus("Обновление отменено"); }
        catch (Exception error) { SetUpdateStatus(error is IOException or InvalidDataException ? error.Message : "Не удалось обновить приложение. Повторите позже.", error: true); }
        finally
        {
            _updateAttempt = null;
            RefreshUpdateActions();
        }
    }
}
