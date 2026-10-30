namespace SneakyFit.Domain;

public enum NutritionMode { Maintain, Lose, Gain }
public enum DayStatus { Planned, Open, Closed }
public enum EntryKind { Planned, Eaten }
public enum ActivityKind { Steps, Workout }
public enum BodySide { None, Left, Right }
public enum BiologicalSex { Unspecified, Female, Male }
public enum FoodUnit { Grams, Millilitres }

public readonly record struct Nutrition(decimal Calories, decimal Protein, decimal Fat, decimal Carbs)
{
    public static Nutrition Zero => new(0, 0, 0, 0);
    public Nutrition ForAmount(decimal amount) => new(Calories * amount / 100, Protein * amount / 100, Fat * amount / 100, Carbs * amount / 100);
    public Nutrition ForGrams(decimal grams) => ForAmount(grams);
    public static Nutrition operator +(Nutrition a, Nutrition b) => new(a.Calories + b.Calories, a.Protein + b.Protein, a.Fat + b.Fat, a.Carbs + b.Carbs);
}

public sealed record MacroSplit(int Protein, int Fat, int Carbs)
{
    public bool IsValid => Protein >= 0 && Fat >= 0 && Carbs >= 0 && Protein + Fat + Carbs == 100;
    public Nutrition AtCalories(decimal calories) => new(calories, calories * Protein / 100 / 4, calories * Fat / 100 / 9, calories * Carbs / 100 / 4);
}

// A day's immutable target snapshot is independent of subsequent profile changes.
public sealed record TargetSnapshot(NutritionMode Mode, decimal Calories, MacroSplit Split, decimal ActivityCreditPercent = 100)
{
    public Nutrition Nutrition => Split.AtCalories(Calories);
    public decimal? MaintenanceCalories { get; init; }
    public decimal ExpectedActivityCalories { get; init; }
    public bool Automatic { get; init; }
}

public sealed record RecipeIngredient(FoodSnapshot Food, decimal Grams);
public sealed record RecipeDefinition(List<RecipeIngredient> Ingredients, decimal RawWeight, decimal CookedWeight)
{
    public FoodUnit Unit { get; init; } = FoodUnit.Grams;
    // Each amount is expressed in its ingredient's own unit; only nutrients are added.
    public Nutrition Total => Ingredients.Aggregate(Nutrition.Zero, (sum, ingredient) => sum + ingredient.Food.Per100.ForAmount(ingredient.Grams));
    public Nutrition Per100 => CookedWeight > 0 ? Total.ForAmount(10000 / CookedWeight) : Nutrition.Zero;
}
public sealed record FoodServing(string Name, decimal Amount);
public sealed record Food(Guid Id, string Name, string Category, Nutrition Per100, bool Favorite = false)
{
    public FoodUnit Unit { get; init; } = FoodUnit.Grams;
    public decimal? GramsPerMillilitre { get; init; }
    public List<FoodServing> Servings { get; init; } = [];
    public RecipeDefinition? Recipe { get; init; }
    public string? CatalogId { get; init; }
    public string? CatalogSource { get; init; }
    public string? Barcode { get; init; }
    public string? Brand { get; init; }
    public string? Preparation { get; init; }
    public string? SourceUrl { get; init; }
    public string? CatalogRevision { get; init; }
}
public sealed record FoodSnapshot(Guid SourceFoodId, string Name, Nutrition Per100)
{
    public FoodUnit Unit { get; init; } = FoodUnit.Grams;
    public decimal? GramsPerMillilitre { get; init; }
}
public sealed record FoodEntry(Guid Id, FoodSnapshot Food, decimal Grams, EntryKind Kind)
{
    // Grams is retained as the serialized amount field for existing profiles.
    public Nutrition Nutrition => Food.Per100.ForAmount(Grams);
}
public sealed record MealTemplate(Guid Id, string Name, TimeOnly Time);
public sealed record Meal(Guid Id, Guid TemplateId, string Name, TimeOnly Time, List<FoodEntry> Entries)
{
    public Nutrition Total(EntryKind kind) => Entries.Where(x => x.Kind == kind).Aggregate(Nutrition.Zero, (sum, x) => sum + x.Nutrition);
}

// Manual calorie estimates are intentionally separate from step counts and durations.
public sealed record ActivityEntry(Guid Id, ActivityKind Kind, string Name, int Steps, int Minutes, decimal EstimatedCalories, TimeOnly Time);
// A personal movement goal, independent of food allowances and calorie-credit settings.
public sealed record ActivityGoalSnapshot(decimal TargetPoints, decimal ReferencePoints, decimal StepPointsPer1000, int ObservedDays)
{
    public bool IsValid => ReferencePoints is > 0 and <= 1000 && TargetPoints > 0 && TargetPoints <= ReferencePoints &&
        StepPointsPer1000 is > 0 and <= 1000 && ObservedDays is >= 0 and <= 21;
}
public sealed record DiaryDay(DateOnly Date, TargetSnapshot Target, DayStatus Status, List<Meal> Meals, List<ActivityEntry> Activities)
{
    public ActivityGoalSnapshot? ActivityGoal { get; init; }
    public bool HasFoodRecord => Meals.Any(m => m.Entries.Any(e => e.Kind == EntryKind.Eaten));
    public bool HasActualRecord => HasFoodRecord || Activities.Count > 0;
    public Nutrition Total(EntryKind kind) => Meals.Aggregate(Nutrition.Zero, (sum, meal) => sum + meal.Total(kind));
    public decimal ActivityCalories(ActivityKind kind) => Activities.Where(x => x.Kind == kind).Sum(x => x.EstimatedCalories) * Target.ActivityCreditPercent / 100;
    public decimal Remaining => Target.Calories - Total(EntryKind.Eaten).Calories;
    public decimal MovementCalories => ActivityCalories(ActivityKind.Steps) + ActivityCalories(ActivityKind.Workout);
    public decimal Budget => Target.Calories + (Status == DayStatus.Planned ? Target.ExpectedActivityCalories : MovementCalories);
    public decimal Available => Budget - Total(EntryKind.Eaten).Calories;
}

public sealed record MeasurementDefinition(string Key, string Name, bool Bilateral);
public sealed record BodyMeasurement(Guid Id, DateTime RecordedAt, string Key, BodySide Side, decimal Value);
public sealed record WeightEntry(Guid Id, DateTime RecordedAt, decimal Kilograms);
public sealed record AdvisorPreferences
{
    public List<string> DisabledRules { get; init; } = ["steps", "training"];
    public DateOnly? DismissedOn { get; init; }
    public List<string> DismissedTasks { get; init; } = [];
    public List<string> SeenInsights { get; init; } = [];
}
public sealed record Profile(Guid Id, string Name, decimal HeightCm, DateOnly BirthDate, BiologicalSex Sex,
    TargetSnapshot DefaultTarget, List<MealTemplate> MealTemplates)
{
    public decimal? GoalWeightKg { get; init; }
    public bool ShowWeightGoal { get; init; } = true;
    public decimal? StepsCaloriesPer1000 { get; init; }
    public AdvisorPreferences Advisor { get; init; } = new();
}

public sealed record Workspace(int SchemaVersion, bool IsDemo, Profile Profile, List<Food> Foods,
    List<DiaryDay> Days, List<WeightEntry> Weight, List<BodyMeasurement> Measurements)
{
    public const int CurrentSchemaVersion = 3;
}

public sealed record BalanceLedger(decimal FoodReserve, decimal StepsCredit, decimal WorkoutsCredit, int ClosedDays)
{
    public decimal Combined => FoodReserve + StepsCredit + WorkoutsCredit;
    public static BalanceLedger Before(IEnumerable<DiaryDay> days, DateOnly before, int windowDays = 7)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var closed = days.Where(d => d.Date >= before.AddDays(-windowDays) && d.Date < before && d.Date < today && d.HasActualRecord).ToArray();
        return new(closed.Where(d => d.HasFoodRecord).Sum(d => d.Remaining), closed.Sum(d => d.ActivityCalories(ActivityKind.Steps)),
            closed.Sum(d => d.ActivityCalories(ActivityKind.Workout)), closed.Length);
    }
}

public static class MeasurementCatalog
{
    public static IReadOnlyList<MeasurementDefinition> All { get; } = [
        new("head", "Обхват головы", false), new("neck", "Обхват шеи", false),
        new("shoulders", "Ширина плеч", false), new("upper_arm", "Обхват плеча / руки", true),
        new("forearm", "Обхват предплечья", true), new("wrist", "Обхват запястья", true),
        new("chest", "Обхват груди", false), new("under_chest", "Обхват под грудью", false),
        new("waist", "Обхват талии", false), new("hips", "Обхват таза / бёдер", false),
        new("buttocks", "Обхват ягодиц", false), new("thigh", "Обхват бедра", true),
        new("calf", "Обхват икры", true), new("ankle", "Обхват щиколотки", true)
    ];
}
