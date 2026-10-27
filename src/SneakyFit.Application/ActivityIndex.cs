using SneakyFit.Domain;

namespace SneakyFit.Application;

public sealed record DailyActivityIndex(int Percent, decimal StepPoints, decimal WorkoutPoints, ActivityGoalSnapshot Goal)
{
    public double Fraction => Math.Clamp((double)((StepPoints + WorkoutPoints) / Goal.TargetPoints), 0, 1);
}

public static class ActivityIndex
{
    private const int HistoryDays = 21;
    private const int MinimumObservations = 7;

    public static DailyActivityIndex? ForDay(Workspace data, DiaryDay day)
    {
        if (day.Date > DateOnly.FromDateTime(DateTime.Now) || day.Activities.Count == 0 || GoalForDay(data, day) is not { } goal) return null;
        var steps = StepPoints(day, goal.StepPointsPer1000);
        var workouts = WorkoutPoints(day);
        var percent = (int)Math.Floor(Math.Clamp((steps + workouts) / goal.TargetPoints * 100, 0, 100));
        return new(percent, steps, workouts, goal);
    }

    public static ActivityGoalSnapshot? GoalForDay(Workspace data, DiaryDay day)
    {
        if (day.Date > DateOnly.FromDateTime(DateTime.Now)) return null;
        if (day.ActivityGoal is { IsValid: true } saved) return saved;
        var reference = ReferenceForDay(data, day.Date);
        if (reference is null) return null;

        // Only recorded, completed dates teach the goal. Missing days are not inactivity.
        var observations = new List<decimal>();
        foreach (var past in data.Days.Where(d => d.Date < day.Date && d.Date >= day.Date.AddDays(-HistoryDays) && d.Activities.Count > 0))
        {
            var basis = past.ActivityGoal is { IsValid: true } snapshot ? snapshot : ReferenceForDay(data, past.Date);
            if (basis is null) continue;
            observations.Add((StepPoints(past, basis.StepPointsPer1000) + WorkoutPoints(past)) / basis.ReferencePoints);
        }
        observations.Sort();
        var desired = observations.Count < MinimumObservations ? 1m : Math.Clamp(Median(observations), .5m, 1m);
        var previous = data.Days.Where(d => d.Date < day.Date && d.ActivityGoal is { IsValid: true })
            .MaxBy(d => d.Date)?.ActivityGoal;
        var previousFactor = previous is null ? 1m : Math.Clamp(previous.TargetPoints / previous.ReferencePoints, .5m, 1m);

        // Ease by at most 5% per recorded day. Progression above an accepted goal will
        // require an explicit choice later; an exceptional workout never raises it.
        var factor = Math.Min(previousFactor, Math.Max(previousFactor * .95m, desired));
        return reference with { TargetPoints = reference.ReferencePoints * factor, ObservedDays = observations.Count };
    }

    public static Workspace FreezeGoals(Workspace data)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var pending = data.Days.Where(d => d.Date <= today && d.Activities.Count > 0 && d.ActivityGoal is not { IsValid: true })
            .OrderBy(d => d.Date).ToArray();
        foreach (var day in pending)
        {
            if (GoalForDay(data, day) is not { } goal) continue;
            data = data with { Days = data.Days.Select(d => d.Date == day.Date ? d with { ActivityGoal = goal } : d).ToList() };
        }
        return data;
    }

    private static ActivityGoalSnapshot? ReferenceForDay(Workspace data, DateOnly date)
    {
        var estimate = SmartTargets.Estimate(data, date);
        if (!estimate.Target.Automatic || estimate.Target.MaintenanceCalories is not > 0 ||
            SmartTargets.WeightAt(data, date) is not > 0) return null;

        // A product goal, not a medical PAL score or a prescription. Count all walking
        // here; the calorie budget still credits only movement above its own baseline.
        var reference = Math.Clamp(estimate.Target.MaintenanceCalories.Value / 1.2m * .15m, 100m, 400m);
        var stepRate = Math.Round(SmartTargets.WeightAt(data, date)!.Value * .0005m * data.Profile.HeightCm * .414m * 10, 1);
        return stepRate > 0 ? new(reference, reference, stepRate, 0) : null;
    }

    private static decimal StepPoints(DiaryDay day, decimal rate) =>
        day.Activities.Where(a => a.Kind == ActivityKind.Steps).Sum(a => (decimal)Math.Max(0, a.Steps)) * rate / 1000;

    private static decimal WorkoutPoints(DiaryDay day) =>
        day.Activities.Where(a => a.Kind == ActivityKind.Workout).Sum(a => Math.Max(0, a.EstimatedCalories));

    private static decimal Median(List<decimal> sorted) => sorted.Count % 2 == 0
        ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2 : sorted[sorted.Count / 2];
}
