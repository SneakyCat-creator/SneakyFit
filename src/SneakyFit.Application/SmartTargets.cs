using SneakyFit.Domain;

namespace SneakyFit.Application;

public sealed record TargetEstimate(TargetSnapshot Target, int ObservedDays, string? Missing);

public static class SmartTargets
{
    // Mifflin–St Jeor resting estimate, sedentary baseline, modest 15% deficit.
    // https://pubmed.ncbi.nlm.nih.gov/2305711/
    // Activity history is a forecast, never an additional credit on top of recorded movement.
    public static TargetEstimate Estimate(Workspace data, DateOnly date) => Estimate(data, date, WeightAt(data, date));

    internal static TargetEstimate Estimate(Workspace data, DateOnly date, decimal? weight)
    {
        var profile = data.Profile;
        var age = date.Year - profile.BirthDate.Year;
        if (profile.BirthDate > date.AddYears(-age)) age--;
        string? missing = profile.Sex == BiologicalSex.Unspecified ? "Укажите пол в профиле" : weight is null ? "Добавьте вес" :
            age < 18 || age > 100 || profile.HeightCm < 100 ? "Авторасчёт недоступен для этих параметров" : null;
        if (missing is not null) return new(profile.DefaultTarget with { Mode = NutritionMode.Lose, Automatic = false, MaintenanceCalories = null, ExpectedActivityCalories = 0, ActivityCreditPercent = 100 }, 0, missing);
        var resting = 10 * weight!.Value + 6.25m * profile.HeightCm - 5 * age + (profile.Sex == BiologicalSex.Male ? 5 : -161);
        var baseline = Math.Max(0, resting * 1.2m);
        var observed = data.Days.Where(d => d.Date < date && d.Date >= date.AddDays(-14) && d.RecordedActivities.Any(a => a.EstimatedCalories.HasValue)).ToArray();
        var expected = observed.Length == 0 ? 0 : observed.Average(d => d.RecordedActivities.Sum(a => a.EstimatedCalories ?? 0));
        // A single exceptional workout must not define the next fortnight's forecast.
        expected = Math.Min(expected, baseline * .5m);
        var bmi = weight.Value / (profile.HeightCm / 100 * (profile.HeightCm / 100));
        var deficit = bmi < 18.5m || profile.GoalWeightKg is { } goal && weight.Value <= goal ? 0 : Math.Min(500, (baseline + expected) * .15m);
        var floor = profile.Sex == BiologicalSex.Male ? 1500m : 1200m;
        var target = profile.DefaultTarget with
        {
            Mode = NutritionMode.Lose, Calories = Math.Round(Math.Max(floor, baseline - deficit)),
            MaintenanceCalories = Math.Round(baseline), ExpectedActivityCalories = Math.Round(expected),
            ActivityCreditPercent = 100, Automatic = true
        };
        return new(target, observed.Length, null);
    }

    public static decimal? WeightAt(Workspace data, DateOnly date) => data.Weight
        .Where(w => DateOnly.FromDateTime(w.RecordedAt) <= date).MaxBy(w => w.RecordedAt)?.Kilograms;

    // Earlier diary days can use the earliest measurement as an approximation for movement.
    // WeightAt stays date-strict for nutrition targets and weight history.
    public static decimal? ActivityWeightAt(Workspace data, DateOnly date) =>
        WeightAt(data, date) ?? data.Weight.MinBy(w => w.RecordedAt)?.Kilograms;

    public static decimal? StepRate(Workspace data, DateOnly date) => data.Profile.StepsCaloriesPer1000 ??
        (data.Profile.HeightCm > 0 && ActivityWeightAt(data, date) is { } kg ? WalkingRate(kg, data.Profile.HeightCm) : null);

    internal static decimal WalkingRate(decimal weightKg, decimal heightCm) => Math.Round(weightKg * .0005m * heightCm * .414m * 10, 1);

    public static decimal? StepCalories(Workspace data, DateOnly date, int steps) => StepCalories(steps, StepRate(data, date));

    internal static decimal? StepCalories(int steps, decimal? ratePer1000) => steps <= 0 ? 0m : steps * ratePer1000 / 1000m;

    public static decimal? WorkoutCalories(decimal? kg, decimal met, int minutes) =>
        kg is { } weight ? Math.Round((met - 1) * weight * minutes / 60) : null;

    public static string Outcome(DiaryDay day)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (day.Date > today) return "План";
        if (day.Date == today) return "День идёт";
        if (!day.HasFoodRecord)
        {
            if (day.Meals.Count > 0 && day.Meals.All(m => m.IsSkipped)) return "Все приёмы пропущены";
            return day.Meals.Any(m => m.IsSkipped) ? "Есть пропущенные приёмы" : "Нет записей о еде";
        }
        if (day.Target.MaintenanceCalories is not { } maintenance)
            return day.Available < -100 ? "Перебор" : day.Available > 100 ? "Ниже нормы" : "В норме";
        var delta = day.Total(EntryKind.Eaten).Calories - maintenance - day.MovementCalories;
        var tolerance = maintenance * .05m;
        return delta > tolerance ? "Перебор" : delta < -tolerance ? "Дефицит" : "Баланс";
    }
}
