using SneakyFit.Domain;

namespace SneakyFit.Application;

public enum AdviceAction { Profile, Weight, Measurement, Meal, Movement }
public sealed record AdviceRule(string Key, string Title, string Detail);
public sealed record AdviceTask(string Id, string Rule, string Title, AdviceAction Action, Guid? MealId = null, string? MeasurementKey = null, BodySide Side = BodySide.None, DateOnly? Date = null);

public static class FitAdvisor
{
    public static IReadOnlyList<AdviceRule> Rules { get; } = [
        new("profile", "Данные профиля", "Если не хватает роста, возраста или пола"),
        new("food", "Приёмы пищи", "Сегодня и пропущенные записи за вчера"),
        new("movement", "Движение к вечеру", "После 20:00, если нет шагов и тренировок"),
        new("weight", "Вес", "Если запись старше недели"),
        new("measurements", "Замеры", "Через месяц · отдельно для каждого параметра"),
        new("steps", "Запись шагов", "После 20:00, если шаги ещё не записаны"),
        new("training", "Тренировки", "Если за неделю нет ни одной записи"),
        new("food-streak", "Серия заполненных дней", "От 3 дней подряд со всеми приёмами пищи"),
        new("activity-streak", "Серия активности", "От 2 дней подряд с индексом не ниже 80"),
        new("weight-progress", "Прогресс веса", "По нескольким записям за последнюю неделю и дольше")
    ];

    public static IReadOnlyList<AdviceTask> Evaluate(Workspace data, DateTime now, bool reviewDay = false)
    {
        var today = DateOnly.FromDateTime(now); var profile = data.Profile;
        var disabled = profile.Advisor?.DisabledRules ?? [];
        var tasks = new List<AdviceTask>();
        void Add(AdviceTask task) { if (!disabled.Contains(task.Rule, StringComparer.Ordinal) && !IsSatisfied(task, data, now)) tasks.Add(task); }
        Add(new("profile", "profile", "Дополнить профиль", AdviceAction.Profile));
        Add(new("weight", "weight", data.Weight.Any(w => w.RecordedAt <= now) ? "Обновить вес" : "Записать вес", AdviceAction.Weight));
        var measures = data.Measurements.Where(m => m.RecordedAt <= now && MeasurementCatalog.All.Any(d => d.Key == m.Key)).GroupBy(m => (m.Key, m.Side)).Select(g => g.MaxBy(m => m.RecordedAt)!).ToList();
        if (measures.Count == 0) Add(new("measure:first", "measurements", "Записать первый замер", AdviceAction.Measurement));
        foreach (var measure in measures)
        {
            var definition = MeasurementCatalog.All.First(d => d.Key == measure.Key);
            var side = measure.Side switch { BodySide.Left => " · слева", BodySide.Right => " · справа", _ => "" };
            Add(new($"measure:{measure.Key}:{measure.Side}", "measurements", definition.Name + side, AdviceAction.Measurement, MeasurementKey: measure.Key, Side: measure.Side));
        }
        // A new calendar day must not make yesterday's unfinished diary disappear.
        // Limit recovery to yesterday rather than generating an unlimited historical debt.
        if (today > DateOnly.MinValue)
        {
            var yesterday = today.AddDays(-1);
            var previous = data.Days.FirstOrDefault(d => d.Date == yesterday);
            var established = data.Days.Any(d => d.Date < yesterday && d.HasActualRecord);
            var touchedYesterday = previous is not null && (previous.HasActualRecord || previous.Meals.Any(m => m.Entries.Count > 0));
            if (touchedYesterday || established) AddMeals(yesterday, previous, previousDay: true);
        }
        AddMeals(today, data.Days.FirstOrDefault(d => d.Date == today), previousDay: false);

        void AddMeals(DateOnly date, DiaryDay? day, bool previousDay)
        {
            var meals = day is { Meals.Count: > 0 }
                ? day.Meals.Select(m => new MealTemplate(m.TemplateId, m.Name, m.Time))
                : profile.MealTemplates;
            var delay = previousDay || reviewDay ? TimeSpan.Zero : TimeSpan.FromHours(6);
            foreach (var meal in meals.Where(m => now - date.ToDateTime(m.Time) >= delay))
            {
                var title = "Записать «" + meal.Name + "»" + (previousDay ? " · вчера" : "");
                Add(new($"meal:{date:yyyy-MM-dd}:{meal.Id}", "food", title, AdviceAction.Meal, meal.Id, Date: date));
            }
        }
        if (now.Hour >= 20)
        {
            Add(new($"movement:{today:yyyy-MM-dd}", "movement", "Как сегодня с движением?", AdviceAction.Movement, Date: today));
            Add(new($"steps:{today:yyyy-MM-dd}", "steps", "Записать шаги", AdviceAction.Movement, Date: today));
            Add(new($"training:{today:yyyy-MM-dd}", "training", "Проверить записи тренировок", AdviceAction.Movement, Date: today));
        }
        return tasks;
    }

    public static bool IsSatisfied(AdviceTask task, Workspace data, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var taskDate = task.Date ?? today;
        if (taskDate > today) return false;
        var activities = data.Days.FirstOrDefault(d => d.Date == taskDate)?.Activities ?? [];
        return task.Rule switch
        {
            "profile" => data.Profile.HeightCm is >= 50 and <= 260 && data.Profile.BirthDate < today && data.Profile.BirthDate >= today.AddYears(-120) && data.Profile.Sex is BiologicalSex.Male or BiologicalSex.Female,
            "weight" => data.Weight.Any(w => w.RecordedAt <= now && now - w.RecordedAt < TimeSpan.FromDays(7)),
            "measurements" => data.Measurements.Any(m => m.RecordedAt <= now && now - m.RecordedAt < TimeSpan.FromDays(30) && (task.MeasurementKey is null || m.Key == task.MeasurementKey && m.Side == task.Side)),
            "food" => data.Days.FirstOrDefault(d => d.Date == taskDate)?.Meals.Any(m => m.TemplateId == task.MealId && m.Entries.Any(e => e.Kind == EntryKind.Eaten)) == true,
            "movement" => activities.Any(a => a.Kind == ActivityKind.Steps && a.Steps > 0 || a.Kind == ActivityKind.Workout && a.Minutes > 0),
            "steps" => activities.Any(a => a.Kind == ActivityKind.Steps),
            "training" => data.Days.Any(d => d.Date <= taskDate && taskDate.DayNumber - d.Date.DayNumber < 7 && d.Activities.Any(a => a.Kind == ActivityKind.Workout && a.Minutes > 0)),
            _ => false
        };
    }
}
