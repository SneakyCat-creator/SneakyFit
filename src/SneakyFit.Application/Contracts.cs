using SneakyFit.Domain;

namespace SneakyFit.Application;

public interface IWorkspaceRepository
{
    Workspace? Load();
    void Save(Workspace workspace);
    void Export(Workspace workspace, string destination);
    Workspace Import(string source);
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string ProfileFile { get; }
}

// Optional future adapters do not participate in the offline core.
public interface IFoodRecognitionProvider
{
    Task<IReadOnlyList<Food>> SuggestAsync(Stream image, CancellationToken cancellationToken);
}
public interface INutritionTargetEstimator
{
    TargetSnapshot Estimate(Profile profile, decimal currentWeightKg, decimal activityFactor, NutritionMode mode);
}
