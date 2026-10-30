using System.Text.Json;
using System.Text.Json.Serialization;
using SneakyFit.Application;
using SneakyFit.Domain;

namespace SneakyFit.Infrastructure;

public sealed class PortablePaths : IAppPaths
{
    public static string RootDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SneakyFit");
    // v1 starts a new profile here. Prototype folders are never imported
    // implicitly; subsequent releases keep this same durable location.
    public string DataDirectory { get; } = Path.Combine(RootDirectory, "UserData");
    public string CacheDirectory { get; } = Path.Combine(RootDirectory, "Cache");
    public string ProfileFile => Path.Combine(DataDirectory, "profile.json");
}

public sealed class JsonWorkspaceRepository(IAppPaths paths) : IWorkspaceRepository
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    public Workspace? Load()
    {
        if (!File.Exists(paths.ProfileFile)) return null;
        var data = Read(paths.ProfileFile);
        using var original = JsonDocument.Parse(File.ReadAllText(paths.ProfileFile));
        if (original.RootElement.GetProperty("SchemaVersion").GetInt32() < Workspace.CurrentSchemaVersion)
        {
            var previous = paths.ProfileFile + $".before-v{Workspace.CurrentSchemaVersion}.bak";
            if (!File.Exists(previous)) File.Copy(paths.ProfileFile, previous);
        }
        return data;
    }
    public void Save(Workspace workspace) => WriteAtomic(paths.ProfileFile, workspace, true);
    public void Export(Workspace workspace, string destination) => WriteAtomic(destination, workspace, false);
    public Workspace Import(string source) => Read(source);

    private static Workspace Read(string path)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new InvalidDataException("Файл профиля больше 25 МБ.");
        var data = JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Не удалось прочитать профиль.");
        if (data.SchemaVersion < 1 || data.SchemaVersion > Workspace.CurrentSchemaVersion) throw new InvalidDataException("Эта версия профиля пока не поддерживается.");
        if (data.Profile is null || data.Foods is null || data.Days is null || data.Weight is null || data.Measurements is null ||
            data.Profile.DefaultTarget is null || data.Profile.DefaultTarget.Split is null || !data.Profile.DefaultTarget.Split.IsValid ||
            data.Profile.DefaultTarget.Calories <= 0 || data.Profile.MealTemplates is null || data.Profile.MealTemplates.Count == 0 ||
            data.Profile.MealTemplates.Any(m => m is null || string.IsNullOrWhiteSpace(m.Name)) ||
            data.Foods.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || !Enum.IsDefined(f.Unit) || !FoodUnits.IsValidDensity(f.GramsPerMillilitre) || !FoodUnits.AreValidServings(f.Servings) ||
                f.Per100.Calories < 0 || f.Per100.Protein < 0 || f.Per100.Fat < 0 || f.Per100.Carbs < 0) ||
            data.Days.Any(d => d is null || d.Target is null || d.Target.Split is null || !d.Target.Split.IsValid || d.Target.Calories <= 0 || d.Target.ActivityCreditPercent < 0 || d.Target.ActivityCreditPercent > 100 || d.Meals is null || d.Activities is null ||
                d.Activities.Any(a => a is null || a.Steps < 0 || a.Minutes < 0 || a.EstimatedCalories < 0) ||
                d.ActivityGoal is { IsValid: false } ||
                d.Meals.Any(m => m is null || m.Entries is null || m.Entries.Any(e => e is null || e.Food is null || !Enum.IsDefined(e.Food.Unit) || !FoodUnits.IsValidDensity(e.Food.GramsPerMillilitre) ||
                    e.Grams <= 0 || e.Grams > 10000 || !Enum.IsDefined(e.Kind) || !ValidNutrition(e.Food.Per100)))))
            throw new InvalidDataException("В файле профиля отсутствуют обязательные данные или есть некорректные значения.");
        if (data.Days.GroupBy(d => d.Date).Any(g => g.Count() > 1) ||
            data.Weight.Any(w => w is null || w.Kilograms <= 0 || w.Kilograms > 500) ||
            data.Measurements.Any(m => m is null || m.Value <= 0 || m.Value > 500 || !MeasurementCatalog.All.Any(def => def.Key == m.Key)))
            throw new InvalidDataException("Проверьте даты дневника, вес и измерения в файле профиля.");
        if (data.Profile.GoalWeightKg is <= 0 or > 500 || data.Profile.StepsCaloriesPer1000 is < 0 or > 300 ||
            data.Foods.Any(f => f.Recipe is { } r && (!Enum.IsDefined(r.Unit) || r.Unit != f.Unit || r.Ingredients is null || r.Ingredients.Count == 0 ||
                r.CookedWeight <= 0 || r.CookedWeight > 100000 || r.RawWeight < 0 || r.RawWeight > 100000 || r.Unit == FoodUnit.Grams && r.RawWeight == 0 ||
                r.Ingredients.Any(i => i is null || i.Food is null || !Enum.IsDefined(i.Food.Unit) || !FoodUnits.IsValidDensity(i.Food.GramsPerMillilitre) || i.Grams <= 0 || i.Grams > 100000 || !ValidNutrition(i.Food.Per100)))))
            throw new InvalidDataException("Проверьте цель веса и состав блюд.");
        var advisor = data.Profile.Advisor ?? new AdvisorPreferences();
        advisor = advisor with
        {
            DisabledRules = (advisor.DisabledRules ?? []).Where(k => FitAdvisor.Rules.Any(r => r.Key == k)).Distinct().ToList(),
            DismissedTasks = (advisor.DismissedTasks ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 160).Distinct().Take(200).ToList(),
            SeenInsights = (advisor.SeenInsights ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200).Distinct().TakeLast(128).ToList()
        };
        return data with { SchemaVersion = Workspace.CurrentSchemaVersion, Profile = data.Profile with { Advisor = advisor },
            Foods = data.Foods.Select(f => f.Recipe is { } r ? f with { Per100 = r.Per100 } : f).ToList() };
    }

    private static bool ValidNutrition(Nutrition value) => value.Calories >= 0 && value.Protein >= 0 && value.Fat >= 0 && value.Carbs >= 0;

    private static void WriteAtomic(string destination, Workspace data, bool backup)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
            if (File.Exists(destination)) File.Replace(temporary, destination, backup ? destination + ".bak" : null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
