using SneakyFit.Domain;

namespace SneakyFit.Application;

public static class WorkspaceFactory
{
    public static Workspace Create()
    {
        var target = new TargetSnapshot(NutritionMode.Lose, 2100, new(25, 30, 45), 100);
        List<MealTemplate> meals =
        [
            new(Guid.NewGuid(), "Завтрак", new(8, 30)),
            new(Guid.NewGuid(), "Обед", new(13, 0)),
            new(Guid.NewGuid(), "Перекус", new(16, 0)),
            new(Guid.NewGuid(), "Ужин", new(19, 30))
        ];
        var profile = new Profile(Guid.NewGuid(), "", 0, DateOnly.MinValue, BiologicalSex.Unspecified, target, meals);
        return new(Workspace.CurrentSchemaVersion, false, profile, [], [], [], []);
    }
}
