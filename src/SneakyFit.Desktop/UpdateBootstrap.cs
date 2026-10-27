using System.Windows;
using SneakyFit.Updates;

namespace SneakyFit.Desktop;

internal static class UpdateBootstrap
{
    public static void ObserveStartup(Window window, string[] args)
    {
        if (args.Length != 3 || args[0] != "--update-started") return;
        EventHandler? rendered = null;
        rendered = (_, _) =>
        {
            window.ContentRendered -= rendered;
            _ = Task.Run(() =>
            {
                // Failure leaves the previous executable intact for recovery.
                try { UpdateHandoff.AcknowledgeStartup(args[1], args[2]); } catch (Exception) { }
            });
        };
        window.ContentRendered += rendered;
    }

    public static bool TryHandle(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        if (args.Length != 3) return true;
        var result = UpdateHandoff.Apply(args[1], args[2]);
        // Before the handoff, the original app displays the failure inline.
        if (result.ParentExited && result.Error is { } error)
            MessageBox.Show(error, "SneakyFit", MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }
}
