using SneakyFit.Domain;

namespace SneakyFit.Application;

public enum FitInsightKind { FoodStreak, ActivityStreak, WeightProgress }
public sealed record FitInsight(string Id, FitInsightKind Kind, DateOnly ThroughDate, int Days, decimal Value)
{
    public string RuleKey => Kind switch
    {
        FitInsightKind.FoodStreak => "food-streak",
        FitInsightKind.ActivityStreak => "activity-streak",
        FitInsightKind.WeightProgress => "weight-progress",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

public static class FitInsights
{
    public static IReadOnlyList<FitInsight> Evaluate(Workspace data, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var days = data.Days.Where(d => d.Date <= today).GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Last());
        var insights = new List<FitInsight>();

        var foodEnd = LastEligibleDay(today, date => days.TryGetValue(date, out var day) && IsFoodComplete(day));
        if (foodEnd is { } end)
        {
            var streak = CountBack(end, date => days.TryGetValue(date, out var day) && IsFoodComplete(day));
            if (streak >= 3) insights.Add(new($"food-streak:{end:yyyy-MM-dd}:{streak}", FitInsightKind.FoodStreak, end, streak, streak));
        }

        // Reuse the app's recorded/personalised index, not raw calories or a new target.
        var activity = new Dictionary<DateOnly, int>();
        foreach (var day in days.Values.Where(d => d.Activities.Count > 0))
            if (ActivityIndex.ForDay(data, day) is { Percent: >= 80 } index) activity[day.Date] = index.Percent;
        var activityEnd = LastEligibleDay(today, activity.ContainsKey);
        if (activityEnd is { } activeEnd)
        {
            var streak = CountBack(activeEnd, activity.ContainsKey);
            if (streak >= 2)
            {
                var average = Math.Round(Enumerable.Range(0, streak).Average(i => (decimal)activity[activeEnd.AddDays(-i)]), 1);
                insights.Add(new($"activity-streak:{activeEnd:yyyy-MM-dd}:{streak}", FitInsightKind.ActivityStreak, activeEnd, streak, average));
            }
        }

        if (WeightProgress(data, now) is { } weight) insights.Add(weight);
        var disabled = data.Profile.Advisor?.DisabledRules ?? [];
        return insights.Where(insight => !disabled.Contains(insight.RuleKey, StringComparer.Ordinal)).ToArray();
    }

    public static bool IsFoodComplete(DiaryDay day) => day.Meals.Count > 0
        && day.Meals.All(meal => meal.Entries.Any(entry => entry.Kind == EntryKind.Eaten)
            && meal.Entries.All(entry => entry.Kind == EntryKind.Eaten));

    private static DateOnly? LastEligibleDay(DateOnly today, Func<DateOnly, bool> eligible)
    {
        if (eligible(today)) return today;
        return today > DateOnly.MinValue && eligible(today.AddDays(-1)) ? today.AddDays(-1) : null;
    }

    private static int CountBack(DateOnly end, Func<DateOnly, bool> eligible)
    {
        var count = 0;
        for (var number = end.DayNumber; number >= 0 && eligible(DateOnly.FromDayNumber(number)); number--) count++;
        return count;
    }

    private static FitInsight? WeightProgress(Workspace data, DateTime now)
    {
        var readings = data.Weight.Where(w => w.RecordedAt <= now && w.Kilograms is > 0 and <= 500)
            .GroupBy(w => DateOnly.FromDateTime(w.RecordedAt)).Select(g => g.MaxBy(w => w.RecordedAt)!)
            .OrderBy(w => w.RecordedAt).ToArray();
        if (readings.Length < 3) return null;
        var latest = readings[^1];
        if (now - latest.RecordedAt >= TimeSpan.FromDays(7)) return null;
        var window = readings.Where(w => latest.RecordedAt - w.RecordedAt <= TimeSpan.FromDays(30)).ToArray();
        if (window.Length < 3) return null;
        var first = window[0];
        var span = (DateOnly.FromDateTime(latest.RecordedAt).DayNumber - DateOnly.FromDateTime(first.RecordedAt).DayNumber);
        if (span < 7) return null;
        var change = first.Kilograms - latest.Kilograms;
        // Do not turn a corrected typo or a one-day fluctuation into a victory message.
        if (change < .2m || change > first.Kilograms * .05m) return null;
        for (var index = 1; index < window.Length; index++)
            if (Math.Abs(window[index].Kilograms - window[index - 1].Kilograms) > window[index - 1].Kilograms * .05m) return null;
        if (data.Profile.HeightCm >= 100 && latest.Kilograms / (data.Profile.HeightCm / 100 * (data.Profile.HeightCm / 100)) < 18.5m) return null;
        if (data.Profile.GoalWeightKg is { } goal && latest.Kilograms < goal) return null;
        var date = DateOnly.FromDateTime(latest.RecordedAt);
        var value = Math.Round(change, 1);
        return new($"weight-progress:{date:yyyy-MM-dd}", FitInsightKind.WeightProgress, date, span, value);
    }
}
