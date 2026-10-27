using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SneakyFit.Application;
using SneakyFit.Domain;
using static SneakyFit.Desktop.Views.D;

namespace SneakyFit.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _advisorTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly CancellationTokenSource _advisorLifetime = new();
    private readonly List<AdviceTask> _adviceTasks = [];
    private readonly HashSet<string> _dismissedAdvice = [];
    private readonly HashSet<string> _sessionInsights = [];
    private bool _advisorReady, _advisorCelebrating, _advisorStarted, _savingAdvisorPreferences;
    private FitInsight? _shownInsight;
    private Guid _advisorProfile;
    private DateOnly _advisorDate;

    private void StartAdvisor()
    {
        Companion.TaskSelected += OpenAdvice;
        Companion.Dismissed += () =>
        {
            foreach (var task in _adviceTasks) _dismissedAdvice.Add(task.Id);
            _adviceTasks.Clear(); _advisorCelebrating = false; _shownInsight = null;
        };
        Companion.CelebrationFinished += () =>
        {
            _adviceTasks.RemoveAll(task => FitAdvisor.IsSatisfied(task, _session.Data, DateTime.Now));
            _advisorCelebrating = false; RefreshAdvisor();
        };
        Companion.InsightFinished += () => { _shownInsight = null; RefreshAdvisor(); };
        _advisorTimer.Tick += (_, _) => RefreshAdvisor(); _advisorTimer.Start();
        ContentRendered += async (_, _) =>
        {
            if (_advisorStarted) return;
            _advisorStarted = true;
            Companion.Checking();
            try { await Task.Delay(650, _advisorLifetime.Token); _advisorReady = true; RefreshAdvisor(reviewDay: true); }
            catch (OperationCanceledException) { }
        };
    }
    private void RefreshAdvisor(bool reviewDay = false)
    {
        if (!_advisorReady || _savingAdvisorPreferences) return;
        var now = DateTime.Now; var today = DateOnly.FromDateTime(now); var profile = _session.Data.Profile;
        if (_advisorProfile != profile.Id || _advisorDate != today)
        {
            if (_advisorProfile != profile.Id) _sessionInsights.Clear();
            _adviceTasks.Clear(); _dismissedAdvice.Clear(); _advisorCelebrating = false; _shownInsight = null;
            _advisorProfile = profile.Id; _advisorDate = today;
            Companion.ResetSession();
            reviewDay = true;
        }
        var preferences = profile.Advisor ?? new();
        _adviceTasks.RemoveAll(t => preferences.DisabledRules.Contains(t.Rule) || _dismissedAdvice.Contains(t.Id) || !AdviceTargetExists(t));
        var evaluated = FitAdvisor.Evaluate(_session.Data, now, reviewDay);
        var missing = evaluated.Where(t => !_dismissedAdvice.Contains(t.Id)).ToList();
        var added = missing.Where(t => _adviceTasks.All(previous => previous.Id != t.Id)).ToList();
        _adviceTasks.AddRange(added);
        var state = _adviceTasks.Select(task => (task, FitAdvisor.IsSatisfied(task, _session.Data, now))).ToList();
        var pending = state.Any(item => !item.Item2);
        if (_advisorCelebrating && !pending && !reviewDay) return;
        _advisorCelebrating = false;

        var insights = FitInsights.Evaluate(_session.Data, now);
        if (_shownInsight is { } current)
        {
            if (!pending && !reviewDay && insights.Any(insight => insight.Id == current.Id)) return;
            _shownInsight = null;
            Companion.ResetSession();
        }

        if (state.Count > 0)
        {
            Companion.SetTasks(state, added.Count > 0 || reviewDay && pending, allClear: false);
            if (!pending) { _advisorCelebrating = true; Companion.Celebrate(); }
            return;
        }

        // Dismissing a question is not the same as completing it. Do not signal
        // that everything is done while the full daily review still has gaps.
        var allClear = FitAdvisor.Evaluate(_session.Data, now, reviewDay: true).Count == 0;
        var next = allClear ? insights.FirstOrDefault(insight => !_sessionInsights.Contains(insight.Id)
            && !(preferences.SeenInsights ?? []).Contains(insight.Id, StringComparer.Ordinal)) : null;
        Companion.SetTasks(state, reveal: false, allClear: allClear && next is null);
        if (next is not null)
        {
            _shownInsight = next;
            RememberInsight(next);
            Companion.ShowInsight(next);
        }
    }
    private bool AdviceTargetExists(AdviceTask task)
    {
        if (task.Action != AdviceAction.Meal) return true;
        var date = task.Date ?? DateOnly.FromDateTime(DateTime.Now);
        var day = _session.Data.Days.FirstOrDefault(d => d.Date == date);
        return day is { Meals.Count: > 0 }
            ? day.Meals.Any(meal => meal.TemplateId == task.MealId)
            : _session.Data.Profile.MealTemplates.Any(meal => meal.Id == task.MealId);
    }
    private void RememberInsight(FitInsight insight)
    {
        _sessionInsights.Add(insight.Id);
        var profile = _session.Data.Profile;
        var preferences = profile.Advisor ?? new();
        var seen = (preferences.SeenInsights ?? []).Where(id => id != insight.Id).Append(insight.Id).TakeLast(128).ToList();
        // SaveProfile raises Changed synchronously; its nested refresh must not
        // replace the message that is about to be shown.
        _savingAdvisorPreferences = true;
        try { _session.SaveProfile(profile with { Advisor = preferences with { SeenInsights = seen } }); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            Notify("Не удалось сохранить историю сообщений фиткота.");
        }
        finally { _savingAdvisorPreferences = false; }
    }
    private void OpenAdvice(AdviceTask task)
    {
        Safe(() =>
        {
            if (!CloseDrawer()) return;
            switch (task.Action)
            {
                case AdviceAction.Profile: _menuTab = "Настройки"; Navigate("profile"); break;
                case AdviceAction.Weight: MeasurementDrawer(false); break;
                case AdviceAction.Measurement:
                    _measurementKey = task.MeasurementKey ?? "waist"; _measurementSide = task.Side; MeasurementDrawer(true); break;
                case AdviceAction.Meal:
                    var date = task.Date ?? DateOnly.FromDateTime(DateTime.Now); SelectDiaryDate(date); if (_page != "diary") Navigate("diary");
                    if (task.MealId is { } id && AdviceTargetExists(task)) MealDrawer(date, id); break;
                case AdviceAction.Movement:
                    SelectDiaryDate(task.Date ?? DateOnly.FromDateTime(DateTime.Now));
                    if (_page != "diary") Navigate("diary");
                    ActivityDrawer(); break;
            }
            Companion.Collapse();
        });
    }
    private UIElement AdvisorSettings()
    {
        var body = S();
        foreach (var rule in FitAdvisor.Rules)
        {
            var check = new CheckBox { Content = S(T(rule.Title, BodySize, Ink, true), M(T(rule.Detail, CaptionSize, "#746A82"), top: 4)), IsChecked = !(_session.Data.Profile.Advisor?.DisabledRules.Contains(rule.Key) ?? false), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 0, 0, 0), Margin = new Thickness(0, 0, 0, 18) };
            check.Click += (_, _) => Safe(() =>
            {
                var preferences = _session.Data.Profile.Advisor ?? new(); var disabled = preferences.DisabledRules.Where(k => k != rule.Key).ToList(); if (check.IsChecked != true) disabled.Add(rule.Key);
                _session.SaveProfile(_session.Data.Profile with { Advisor = preferences with { DisabledRules = disabled } });
            });
            body.Children.Add(check);
        }
        body.Children.Add(Btn("Проверить сейчас", () => Safe(() =>
        {
            _dismissedAdvice.Clear(); RefreshAdvisor(reviewDay: true);
        }), "#EEE8F8", Ink));
        return body;
    }
}
