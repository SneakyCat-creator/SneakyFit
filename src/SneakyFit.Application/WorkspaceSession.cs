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
        Data = ActivityIndex.FreezeGoals(FillActivityEstimates(workspace));
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
        if (count is < 0 or > 200000) throw new ArgumentException("Укажите от 0 до 200 000 шагов.");
        var day = Day(date); var previous = day.Activities.FirstOrDefault(a => a.Kind == ActivityKind.Steps);
        var rate = SmartTargets.StepRate(Data, date);
        var calories = SmartTargets.StepCalories(count, rate);
        var planned = date > DateOnly.FromDateTime(DateTime.Now);
        if (previous is not null && previous.Steps == count && previous.EstimatedCalories == calories && previous.StepCaloriesPer1000 == rate && previous.Planned == planned) return;
        var entry = new ActivityEntry(previous?.Id ?? Guid.NewGuid(), ActivityKind.Steps, "Шаги", count, 0, calories, TimeOnly.FromDateTime(DateTime.Now))
            { StepCaloriesPer1000 = rate, Planned = planned };
        UpdateDay(day with { Activities = [.. day.Activities.Where(a => a.Kind != ActivityKind.Steps), entry] });
    }

    public void SaveWorkout(DateOnly date, string name, decimal met, int minutes, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name) || met is < 1 or > 30 || minutes is < 1 or > 600)
            throw new ArgumentException("Проверьте занятие и продолжительность тренировки.");
        var day = Day(date);
        var previous = id is null ? null : day.Activities.FirstOrDefault(a => a.Id == id && a.Kind == ActivityKind.Workout)
            ?? throw new ArgumentException("Тренировка больше не найдена.");
        var calories = SmartTargets.WorkoutCalories(SmartTargets.ActivityWeightAt(Data, date), met, minutes);
        var entry = new ActivityEntry(previous?.Id ?? Guid.NewGuid(), ActivityKind.Workout, name, 0, minutes, calories, previous?.Time ?? TimeOnly.FromDateTime(DateTime.Now))
            { WorkoutMet = met, Planned = date > DateOnly.FromDateTime(DateTime.Now) };
        UpdateDay(day with { Activities = previous is null ? [.. day.Activities, entry] :
            day.Activities.Select(a => a.Id == entry.Id ? entry : a).ToList() });
    }

    public void ConfirmActivityPlan(DateOnly date)
    {
        if (date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Отметить выполнение можно в день занятия.");
        var day = Day(date);
        if (day.Activities.Any(a => a.Planned))
            UpdateDay(day with { Activities = day.Activities.Select(a => a.Planned ? a with { Planned = false } : a).ToList() });
    }

    public void AddFoodToMeal(DateOnly date, Guid templateId, Food food, decimal grams, EntryKind kind)
    {
        if (!Enum.IsDefined(food.Unit) || !FoodUnits.IsValidDensity(food.GramsPerMillilitre)) throw new ArgumentException("Проверьте единицу продукта.");
        if (grams <= 0 || grams > 10000) throw new ArgumentException($"Укажите порцию до 10 000 {FoodUnits.Symbol(food.Unit)}.");
        if (kind == EntryKind.Eaten && date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Будущие приёмы пищи можно только запланировать.");
        var day = Day(date);
        var entry = new FoodEntry(Guid.NewGuid(), new(food.Id, food.Name, food.Per100) { Unit = food.Unit, GramsPerMillilitre = food.GramsPerMillilitre, Details = food.Details }, grams, kind) { AddedAt = DateTimeOffset.UtcNow };
        if (!day.Meals.Any(m => m.TemplateId == templateId)) throw new ArgumentException("Приём пищи больше не найден.");
        var foods = Data.Foods.Any(f => f.Id == food.Id) ? Data.Foods : [.. Data.Foods, food];
        var updated = day with { Meals = day.Meals.Select(m => m.TemplateId == templateId ? m with { IsSkipped = false, Entries = [.. m.Entries, entry] } : m).ToList() };
        Commit(Data with { Foods = foods, Days = Data.Days.Where(d => d.Date != date).Append(updated).OrderBy(d => d.Date).ToList() });
    }

    public void SetMealSkipped(DateOnly date, Guid templateId, bool skipped)
    {
        var day = Day(date);
        var meal = day.Meals.FirstOrDefault(m => m.TemplateId == templateId)
            ?? throw new ArgumentException("Приём пищи больше не найден.");
        if (meal.IsSkipped == skipped) return;
        UpdateDay(day with { Meals = day.Meals.Select(m => m.TemplateId == templateId ? m with { IsSkipped = skipped } : m).ToList() });
    }

    public void RemoveEntry(DateOnly date, Guid entryId)
    {
        var day = Day(date);
        UpdateDay(day with { Meals = day.Meals.Select(m => m with { Entries = m.Entries.Where(e => e.Id != entryId).ToList() }).ToList() });
    }

    public void EditEntry(DateOnly date, Guid entryId, string name, Nutrition per100, decimal grams, EntryKind kind, FoodUnit? unit = null, decimal? gramsPerMillilitre = null, FoodDetails? details = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Введите название до 200 символов.");
        if (!Enum.IsDefined(kind) || kind == EntryKind.Eaten && date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Будущие приёмы можно только планировать.");
        var day = Day(date);
        var existing = day.Meals.SelectMany(m => m.Entries).FirstOrDefault(e => e.Id == entryId)
            ?? throw new ArgumentException("Запись больше не найдена.");
        if (unit is { } chosen && !Enum.IsDefined(chosen) || !FoodUnits.IsValidDensity(gramsPerMillilitre)) throw new ArgumentException("Проверьте единицу продукта.");
        if (grams <= 0 || grams > 10000) throw new ArgumentException($"Укажите порцию до 10 000 {FoodUnits.Symbol(existing.Food.Unit)}.");
        if (per100.Calories is < 0 or > 1500 || new[] { per100.Protein, per100.Fat, per100.Carbs }.Any(n => n is < 0 or > 100)) throw new ArgumentException($"Проверьте КБЖУ на 100 {FoodUnits.Symbol(existing.Food.Unit)}.");
        if (!FoodDetails.IsValid(details)) throw new ArgumentException("Проверьте подробные данные продукта.");
        // Only this serving changes; catalog products, recipes and other dates keep their snapshots.
        UpdateDay(day with { Meals = day.Meals.Select(m => m with { Entries = m.Entries.Select(e => e.Id == entryId
            ? e with { Food = e.Food with { Name = name.Trim(), Per100 = per100, Unit = unit ?? e.Food.Unit, GramsPerMillilitre = gramsPerMillilitre ?? e.Food.GramsPerMillilitre, Details = details ?? e.Food.Details }, Grams = grams, Kind = kind } : e).ToList() }).ToList() });
    }

    public void EatPlannedMeal(DateOnly date, Guid templateId)
    {
        if (date > DateOnly.FromDateTime(DateTime.Now)) throw new ArgumentException("Этот приём пищи запланирован на будущий день.");
        var day = Day(date);
        UpdateDay(day with { Meals = day.Meals.Select(m => m.TemplateId == templateId
            ? m with { IsSkipped = false, Entries = m.Entries.Select(e => e.Kind == EntryKind.Planned ? e with { Kind = EntryKind.Eaten } : e).ToList() } : m).ToList() });
    }

    public void CopyMenu(DateOnly from, DateOnly to, bool replacePlan = false)
    {
        if (from == to) throw new ArgumentException("Выберите другой день для копирования.");
        var source = Day(from);
        var destination = Day(to);
        var sourceMeals = source.Meals.Where(m => m.ActiveEntries.Any()).ToArray();
        if (sourceMeals.Length == 0) throw new ArgumentException("В этом дне нет блюд для переноса.");
        var meals = destination.Meals.Select(m => replacePlan && !m.IsSkipped ? m with { Entries = m.Entries.Where(e => e.Kind == EntryKind.Eaten).ToList() } : m).ToList();
        foreach (var meal in sourceMeals)
        {
            var index = meals.FindIndex(m => m.TemplateId == meal.TemplateId || m.Name == meal.Name);
            if (index >= 0 && meals[index].IsSkipped) continue;
            var entries = meal.Entries.Select(e => e with { Id = Guid.NewGuid(), Kind = EntryKind.Planned, AddedAt = DateTimeOffset.UtcNow }).ToList();
            if (index >= 0) meals[index] = meals[index] with { Entries = [.. meals[index].Entries, .. entries] };
            else meals.Add(meal with { Id = Guid.NewGuid(), Entries = entries });
        }
        UpdateDay(destination with { Meals = meals });
    }

    public List<Guid> RecentFoodIds() => Data.Days.SelectMany(day => day.Meals.SelectMany(meal => meal.ActiveEntries
        .Select(entry => (Entry: entry, RecordedAt: entry.AddedAt ?? new DateTimeOffset(day.Date.ToDateTime(meal.Time), TimeSpan.Zero)))))
        .OrderByDescending(item => item.RecordedAt).Select(item => item.Entry.Food.SourceFoodId).Distinct().ToList();

    public void SaveFood(Food food)
    {
        if (!FoodDetails.IsValid(food.Details)) throw new ArgumentException("Проверьте подробные данные продукта.");
        if (!Enum.IsDefined(food.Unit) || !FoodUnits.AreValidServings(food.Servings) || !FoodUnits.IsValidDensity(food.GramsPerMillilitre)) throw new ArgumentException("Проверьте единицу продукта и порции.");
        if (food.Recipe is { } recipe)
        {
            if (!Enum.IsDefined(recipe.Unit) || recipe.Ingredients is null || recipe.Ingredients.Count == 0 ||
                recipe.CookedWeight <= 0 || recipe.CookedWeight > 100000 || recipe.RawWeight < 0 || recipe.RawWeight > 100000 ||
                recipe.Unit == FoodUnit.Grams && recipe.RawWeight == 0 ||
                recipe.Ingredients.Any(i => i is null || i.Food is null || !Enum.IsDefined(i.Food.Unit) || !FoodUnits.IsValidDensity(i.Food.GramsPerMillilitre) ||
                    i.Grams <= 0 || i.Grams > 100000 || new[] { i.Food.Per100.Calories, i.Food.Per100.Protein, i.Food.Per100.Fat, i.Food.Per100.Carbs }.Any(v => v < 0)))
                throw new ArgumentException("Проверьте ингредиенты и количество готового блюда.");
            food = food with { Unit = recipe.Unit, Per100 = recipe.Per100, Details = FoodDetails.ForRecipe(recipe) };
            if (!FoodDetails.IsValid(food.Details) || food.Per100.Calories > 1500 ||
                food.Per100.Protein + food.Per100.Fat + food.Per100.Carbs > 105)
                throw new ArgumentException("Проверьте вес готового блюда и количество ингредиентов.");
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
        target = target with { Meals = target.Meals.Select(m => m.TemplateId == templateId ? m with { IsSkipped = false, Entries = [.. m.Entries, entry] } : m).ToList() };
        var days = Data.Days.Where(d => d.Date != from && d.Date != to).ToList();
        if (from != to) days.Add(changedSource);
        days.Add(target);
        Commit(Data with { Days = days.OrderBy(d => d.Date).ToList() });
    }

    public Food SaveCatalogFood(CatalogProduct product)
    {
        var imported = product.ToFood();
        var existing = Data.Foods.FirstOrDefault(f => f.CatalogId == imported.CatalogId);
        if (existing is not null) imported = imported with
        {
            Id = existing.Id, Favorite = existing.Favorite,
            Servings = imported.Unit == existing.Unit ? existing.Servings.ToList() : [],
            Details = imported.Details ?? existing.Details,
            GramsPerMillilitre = imported.GramsPerMillilitre ?? existing.GramsPerMillilitre
        };
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
            else foods[index] = food with
            {
                Id = foods[index].Id, Favorite = foods[index].Favorite,
                Servings = food.Unit == foods[index].Unit ? foods[index].Servings.ToList() : [],
                Details = food.Details ?? foods[index].Details,
                GramsPerMillilitre = food.GramsPerMillilitre ?? foods[index].GramsPerMillilitre
            };
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

    public void Replace(Workspace data) => Commit(data, refreshActivities: true);
    public void Export(string path) => repository.Export(Data, path);
    public void Import(string path) => Replace(repository.Import(path));

    private void Commit(Workspace data, bool refreshActivities = false)
    {
        if (refreshActivities || Data.Weight != data.Weight || Data.Profile.HeightCm != data.Profile.HeightCm || Data.Profile.StepsCaloriesPer1000 != data.Profile.StepsCaloriesPer1000)
            data = FillActivityEstimates(data);
        data = ActivityIndex.FreezeGoals(data);
        // Save first: a failed write never reports a successful in-memory change.
        repository.Save(data);
        Data = data;
        Changed?.Invoke();
    }

    private static Workspace FillActivityEstimates(Workspace data)
    {
        var days = data.Days.ToList();
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            if (day.Activities.Count == 0) continue;
            var activities = day.Activities.Select(entry =>
            {
                if (entry.Kind == ActivityKind.Steps && (entry.StepCaloriesPer1000 is null || entry.EstimatedCalories is null))
                {
                    var rate = entry.StepCaloriesPer1000 ?? RecoverStepRate(data, day, entry);
                    return entry with { StepCaloriesPer1000 = rate, EstimatedCalories = SmartTargets.StepCalories(entry.Steps, rate) };
                }
                if (entry.Kind == ActivityKind.Workout && entry.EstimatedCalories is null && entry.WorkoutMet is { } met)
                    return entry with { EstimatedCalories = SmartTargets.WorkoutCalories(SmartTargets.ActivityWeightAt(data, day.Date), met, entry.Minutes) };
                return entry;
            }).ToList();
            if (!day.Activities.SequenceEqual(activities)) days[i] = day with { Activities = activities };
        }
        return data.Days.SequenceEqual(days) ? data : data with { Days = days };
    }

    private static decimal? RecoverStepRate(Workspace data, DiaryDay day, ActivityEntry entry)
    {
        var frozenRate = day.ActivityGoal?.StepPointsPer1000;
        var rate = data.Profile.StepsCaloriesPer1000 ?? frozenRate ?? SmartTargets.StepRate(data, day.Date);
        // Older entries stored rounded kcal for the steps above 2,000 without the original rate.
        var counted = entry.Steps - 2000;
        if (entry.EstimatedCalories is not { } saved || saved <= 0 || entry.Steps == 0) return rate;
        // An older client can also re-save a corrected total while dropping the optional rate.
        decimal ImpliedRate(int steps) => Math.Min(saved, steps * 300m / 1000) * 1000 / steps;
        if (counted <= 0) return ImpliedRate(entry.Steps);
        bool Matches(decimal? candidate) => candidate is { } value &&
            (Math.Round(counted * value / 1000) == saved || SmartTargets.StepCalories(entry.Steps, value) == saved);
        if (Matches(rate)) return rate;
        if (Matches(frozenRate)) return frozenRate;
        return Math.Round(ImpliedRate(counted), 1);
    }
}
