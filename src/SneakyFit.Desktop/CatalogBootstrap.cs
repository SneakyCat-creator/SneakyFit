using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace SneakyFit.Desktop;

internal static class CatalogBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() => AssemblyLoadContext.Default.Resolving += Resolve;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (!string.Equals(name.Name, "zxing", StringComparison.OrdinalIgnoreCase)) return null;
        using var resource = typeof(CatalogBootstrap).Assembly.GetManifestResourceStream("SneakyFit.Dependencies.zxing.dll");
        return resource is null ? null : context.LoadFromStream(resource);
    }
}
