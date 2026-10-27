using SneakyFit.Domain;

namespace SneakyFit.Application;

// The presentation layer performs actions through this session, not through disk storage.
public sealed class WorkspaceSession
{
    private readonly IWorkspaceRepository repository;
    public Workspace Data { get; private set; }

    public WorkspaceSession(IWorkspaceRepository repository, Workspace workspace)
    {
        this.repository = repository;
        Data = ActivityIndex.FreezeGoals(workspace);
        if (!ReferenceEquals(Data, workspace)) repository.Save(Data);
    }
    public event Action? Changed;
    public DateOnly SelectedDate { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    public DiaryDay Day(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stored = Data.Days.FirstOrDefault(x => x.Date == date);
        var day = stored ?? new(date, Data.Profile.DefaultTarget, date > today ? DayStatus.Planned : DayStatus.Open,
            Data.Profile.MealTemplates.Select(t => new Meal(Guid.NewGuid(), t.Id, t.Name, t.Time, [])).ToList(), []);
        if (stored is not null && date < today)
        {
            // Preserve historical food budgets; fill only a missing expenditure estimate.
            if (day.Target.MaintenanceCalories is null)
                day = day with { Target = day.Target with { MaintenanceCalories = SmartTargets.Estimate(Data, date).Target.MaintenanceCalories } };
            return day with { Status = day.HasActualRecord ? DayStatus.Closed : DayStatus.Open };
        }
        return day with { Target = SmartTargets.Estimate(Data, date).Target, Status = date > today ? DayStatus.Planned : DayStatus.Open };
    }

    public void UpdateDay(DiaryDay day)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        day = day with { Status = day.Date > today ? DayStatus.Planned : day.Date < today && day.HasActualRecord ? DayStatus.Closed : DayStatus.Open };
        Commit(Data with { Days = Data.Days.Where(d => d.Date != day.Date).Append(day).OrderBy(d => d.Date).ToList() });
    }

    public void SaveSteps(DateOnly date, int count)
    {
        if (date > DateOnly.FromDateTime(DateTime.Now) || count < 0 || count > 200000) throw new ArgumentException("Проверьте дату и количество шагов.");
        var day = Day(date); var previous = day.Activities.FirstOrDefault(a => a.Kind == ActivityKind.Steps);
        var calories = SmartTargets.StepCalories(Data, date, count);
        if (previous is not null && previous.Steps == count && previous.EstimatedCalories == calories) return;
        var entry = new ActivityEntry(previous?.Id ?? Guid.NewGuid(), ActivityKind.Steps, "Шаги", count, 0, calories, TimeOnly.FromDateTime(DateTime.Now));
        UpdateDay(day with { Activities = [.. day.Activities.Where(a => a.Kind != ActivityKind.Steps), entry] });
    }

    public void AddFoodToMeal(DateOnly date, Guid templateId, Food food, decimal grams, EntryKind kind)
    {
        if (grams <= 0 || grams > 10000) throw new ArgumentException("Укажите порцию от 0 до 10 000 г.");
        if (kind == EntryKind.Eaten && date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Будущие приёмы пищи можно только запланировать.");
        var day = Day(date);
        var entry = new FoodEntry(Guid.NewGuid(), new(food.Id, food.Name, food.Per100), grams, kind);
        UpdateDay(day with { Meals = day.Meals.Select(m => m.TemplateId == templateId ? m with { Entries = [.. m.Entries, entry] } : m).ToList() });
    }

    public void RemoveEntry(DateOnly date, Guid entryId)
    {
        var day = Day(date);
        UpdateDay(day with { Meals = day.Meals.Select(m => m with { Entries = m.Entries.Where(e => e.Id != entryId).ToList() }).ToList() });
    }

    public void EditEntry(DateOnly date, Guid entryId, string name, Nutrition per100, decimal grams, EntryKind kind)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Введите название до 200 символов.");
        if (grams <= 0 || grams > 10000) throw new ArgumentException("Укажите порцию до 10 000 г.");
        if (per100.Calories is < 0 or > 1000 || new[] { per100.Protein, per100.Fat, per100.Carbs }.Any(n => n is < 0 or > 100)) throw new ArgumentException("Проверьте КБЖУ на 100 г.");
        if (!Enum.IsDefined(kind) || kind == EntryKind.Eaten && date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Будущие приёмы можно только планировать.");
        var day = Day(date);
        if (!day.Meals.Any(m => m.Entries.Any(e => e.Id == entryId))) throw new ArgumentException("Запись больше не найдена.");
        // Only this serving changes; catalog products, recipes and other dates keep their snapshots.
        UpdateDay(day with { Meals = day.Meals.Select(m => m with { Entries = m.Entries.Select(e => e.Id == entryId
            ? e with { Food = e.Food with { Name = name.Trim(), Per100 = per100 }, Grams = grams, Kind = kind } : e).ToList() }).ToList() });
    }

    public void EatPlannedMeal(DateOnly date, Guid templateId)
    {
        if (date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Этот приём пищи запланирован на будущий день.");
        var day = Day(date);
        UpdateDay(day with { Meals = day.Meals.Select(m => m.TemplateId == templateId
            ? m with { Entries = m.Entries.Select(e => e.Kind == EntryKind.Planned ? e with { Kind = EntryKind.Eaten } : e).ToList() } : m).ToList() });
    }

    public void CopyMenu(DateOnly from, DateOnly to, bool replacePlan = false)
    {
        if (from == to) throw new ArgumentException("Выберите другой день для копирования.");
        var source = Day(from);
        var destination = Day(to);
        if (!source.Meals.Any(m => m.Entries.Count > 0)) throw new ArgumentException("В этом дне нет блюд.");
        var meals = destination.Meals.Select(m => replacePlan ? m with { Entries = m.Entries.Where(e => e.Kind == EntryKind.Eaten).ToList() } : m).ToList();
        foreach (var meal in source.Meals.Where(m => m.Entries.Count > 0))
        {
            var index = meals.FindIndex(m => m.TemplateId == meal.TemplateId || m.Name == meal.Name);
            var entries = meal.Entries.Select(e => e with { Id = Guid.NewGuid(), Kind = EntryKind.Planned }).ToList();
            if (index >= 0) meals[index] = meals[index] with { Entries = [.. meals[index].Entries, .. entries] };
            else meals.Add(meal with { Id = Guid.NewGuid(), Entries = entries });
        }
        UpdateDay(destination with { Meals = meals });
    }

    public void SaveFood(Food food)
    {
        if (food.Recipe is { } recipe)
        {
            if (recipe.Ingredients.Count == 0 || recipe.CookedWeight <= 0 || recipe.RawWeight <= 0 || recipe.Ingredients.Any(i => i.Grams <= 0)) throw new ArgumentException("Добавьте ингредиенты и вес готового блюда.");
            food = food with { Per100 = recipe.Per100 };
        }
        if (string.IsNullOrWhiteSpace(food.Name)) throw new ArgumentException("Введите название блюда.");
        if (new[] { food.Per100.Calories, food.Per100.Protein, food.Per100.Fat, food.Per100.Carbs }.Any(v => v < 0)) throw new ArgumentException("КБЖУ не могут быть отрицательными.");
        Commit(Data with { Foods = Data.Foods.Where(f => f.Id != food.Id).Append(food).ToList() });
    }

    public void MovePlannedEntry(DateOnly from, Guid entryId, DateOnly to, Guid templateId)
    {
        var source = Day(from);
        var entry = source.Meals.SelectMany(m => m.Entries).FirstOrDefault(e => e.Id == entryId && e.Kind == EntryKind.Planned)
            ?? throw new ArgumentException("Запись больше не найдена.");
        var changedSource = source with { Meals = source.Meals.Select(m => m with { Entries = m.Entries.Where(e => e.Id != entryId).ToList() }).ToList() };
        var target = from == to ? changedSource : Day(to);
        if (!target.Meals.Any(m => m.TemplateId == templateId)) throw new ArgumentException("Приём пищи больше не найден.");
        target = target with { Meals = target.Meals.Select(m => m.TemplateId == templateId ? m with { Entries = [.. m.Entries, entry] } : m).ToList() };
        var days = Data.Days.Where(d => d.Date != from && d.Date != to).ToList();
        if (from != to) days.Add(changedSource);
        days.Add(target);
        Commit(Data with { Days = days.OrderBy(d => d.Date).ToList() });
    }

    public Food SaveCatalogFood(CatalogProduct product)
    {
        var imported = product.ToFood();
        var existing = Data.Foods.FirstOrDefault(f => f.CatalogId == imported.CatalogId);
        if (existing is not null) imported = imported with { Id = existing.Id, Favorite = existing.Favorite };
        SaveFood(imported);
        return imported;
    }

    public void MergeCatalogFoods(IEnumerable<CatalogProduct> products)
    {
        var foods = Data.Foods.ToList();
        foreach (var product in products.Where(p => p.HasCompleteNutrition))
        {
            var food = product.ToFood(); var index = foods.FindIndex(f => f.CatalogId == food.CatalogId);
            if (index < 0) foods.Add(food);
            else foods[index] = food with { Id = foods[index].Id, Favorite = foods[index].Favorite };
        }
        // Historical diary/recipe snapshots deliberately stay unchanged.
        Commit(Data with { Foods = foods });
    }

    public void SaveProfile(Profile profile, WeightEntry? weight = null)
    {
        if (!profile.DefaultTarget.Split.IsValid) throw new ArgumentException("Сумма процентов БЖУ должна быть равна 100%.");
        if (profile.DefaultTarget.Calories <= 0) throw new ArgumentException("Укажите положительную норму калорий.");
        if (profile.GoalWeightKg is <= 0 or > 500 || profile.StepsCaloriesPer1000 is < 0 or > 300) throw new ArgumentException("Проверьте цель веса и расчёт шагов.");
        if (weight is not null && (weight.Kilograms <= 0 || weight.Kilograms > 500 || weight.RecordedAt > DateTime.Now)) throw new ArgumentException("Проверьте вес и дату записи.");
        Commit(Data with { Profile = profile, Weight = weight is null ? Data.Weight : Data.Weight.Where(w => w.Id != weight.Id).Append(weight).OrderBy(w => w.RecordedAt).ToList() });
    }

    public void SaveWeight(WeightEntry entry)
    {
        if (entry.Kilograms <= 0 || entry.Kilograms > 500) throw new ArgumentException("Проверьте значение веса.");
        if (entry.RecordedAt > DateTime.Now) throw new ArgumentException("Выберите прошедшее время.");
        Commit(Data with { Weight = Data.Weight.Where(w => w.Id != entry.Id).Append(entry).OrderBy(x => x.RecordedAt).ToList() });
    }

    public void SaveMeasurement(BodyMeasurement entry)
    {
        if (entry.Value <= 0 || entry.Value > 500) throw new ArgumentException("Проверьте значение измерения.");
        if (entry.RecordedAt > DateTime.Now) throw new ArgumentException("Выберите прошедшее время.");
        var definition = MeasurementCatalog.All.FirstOrDefault(d => d.Key == entry.Key) ?? throw new ArgumentException("Неизвестный параметр.");
        if (definition.Bilateral ? entry.Side == BodySide.None : entry.Side != BodySide.None) throw new ArgumentException("Проверьте сторону измерения.");
        Commit(Data with { Measurements = Data.Measurements.Where(m => m.Id != entry.Id).Append(entry).OrderBy(x => x.RecordedAt).ToList() });
    }

    public void DeleteWeight(Guid id) => Commit(Data with { Weight = Data.Weight.Where(w => w.Id != id).ToList() });
    public void DeleteMeasurement(Guid id) => Commit(Data with { Measurements = Data.Measurements.Where(m => m.Id != id).ToList() });

    public void Replace(Workspace data) => Commit(data);
    public void Export(string path) => repository.Export(Data, path);
    public void Import(string path) => Commit(repository.Import(path));

    private void Commit(Workspace data)
    {
        data = ActivityIndex.FreezeGoals(data);
        // Save first: a failed write never reports a successful in-memory change.
        repository.Save(data);
        Data = data;
        Changed?.Invoke();
    }
}
