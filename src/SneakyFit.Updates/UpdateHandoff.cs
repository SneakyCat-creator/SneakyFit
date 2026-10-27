using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SneakyFit.Updates;

public sealed record UpdateTicket(int ParentId, long ParentStartTicks, string Target, string CurrentHash, string NewHash, string Version);
public sealed record UpdateApplyResult(bool ParentExited, string? Error);
internal sealed record UpdateStartupReceipt(int ProcessId, long StartTicks, string TicketHash);

public static class UpdateHandoff
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SneakyFit", "Cache", "Updates");

    public static string NewDirectory()
    {
        Directory.CreateDirectory(Root);
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        return directory;
    }

    public static void RemoveDirectory(string directory)
    {
        try
        {
            ValidateDirectory(directory);
            if (!Directory.Exists(directory)) return;
            var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            if (entries.Any(entry => IsLink(entry) || Directory.Exists(entry))) return;
            foreach (var file in entries) File.Delete(file);
            Directory.Delete(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static void CleanOldDownloads()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            try { if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) RemoveDirectory(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task Prepare(DownloadedUpdate update, CancellationToken cancellation)
    {
        ValidateDirectory(update.Directory);
        var target = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("Не найден файл SneakyFit."));
        if (!string.Equals(Path.GetFullPath(update.Executable), Path.Combine(update.Directory, "download.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Некорректная папка обновления.");
        var current = await Task.Run(() => ReleaseTrust.VerifyExecutable(target), cancellation).ConfigureAwait(false);
        if (Version.Parse(update.Release.Version) <= Version.Parse(current.Version)) throw new IOException("Эта версия уже установлена.");
        using var parent = Process.GetCurrentProcess();
        var ticket = new UpdateTicket(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target,
            ReleaseTrust.HashFile(target), ReleaseTrust.HashFile(update.Executable), update.Release.Version);
        var work = Path.Combine(update.Directory, "work.json");
        await File.WriteAllTextAsync(work, JsonSerializer.Serialize(ticket), new UTF8Encoding(false), cancellation).ConfigureAwait(false);
        var helper = Path.Combine(update.Directory, "runner.exe");
        await Copy(target, helper, cancellation).ConfigureAwait(false);
        if (!ReleaseTrust.HashFile(helper).Equals(ticket.CurrentHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Не удалось подготовить обновление.");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = update.Directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(work); start.ArgumentList.Add(ReleaseTrust.HashFile(work));
        using var process = Process.Start(start) ?? throw new IOException("Не удалось начать обновление.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(Path.Combine(update.Directory, "ready")))
            {
                if (process.HasExited) throw new IOException(ReadError(update.Directory));
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(update.Directory, "proceed"), "1", cancellation).ConfigureAwait(false);
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(update.Directory, "cancel"), "1").ConfigureAwait(false);
            throw;
        }
    }

    public static UpdateApplyResult Apply(string work, string ticketHash)
    {
        string? directory = null, staged = null, backup = null;
        Process? replacement = null;
        UpdateTicket? ticket = null; var replaced = false; var parentExited = false; var stagedCreated = false;
        try
        {
            work = Path.GetFullPath(work); var candidateDirectory = Path.GetDirectoryName(work)!; ValidateDirectory(candidateDirectory); directory = candidateDirectory;
            if (!ReleaseTrust.IsHash(ticketHash) || Path.GetFileName(work) != "work.json" || new FileInfo(work).Length > 16 * 1024)
                throw new IOException("Запрос обновления изменился.");
            var ticketBytes = File.ReadAllBytes(work);
            if (ticketBytes.Length > 16 * 1024 || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ticketBytes)).Equals(ticketHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Запрос обновления изменился.");
            ticket = JsonSerializer.Deserialize<UpdateTicket>(ticketBytes) ?? throw new IOException("Запрос обновления повреждён.");
            var runner = Path.Combine(directory, "runner.exe"); var downloaded = Path.Combine(directory, "download.exe");
            if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), runner, StringComparison.OrdinalIgnoreCase) ||
                !ReleaseTrust.IsHash(ticket.CurrentHash) || !ReleaseTrust.IsHash(ticket.NewHash) ||
                !ReleaseTrust.HashFile(runner).Equals(ticket.CurrentHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Неизвестный установщик обновления.");
            var target = Path.GetFullPath(ticket.Target);
            if (target != ticket.Target || Path.GetExtension(target).ToLowerInvariant() != ".exe" || target.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || IsLink(target))
                throw new IOException("Некорректный путь SneakyFit.");
            var before = ReleaseTrust.VerifyExecutable(target); var after = ReleaseTrust.VerifyExecutable(downloaded);
            if (!ReleaseTrust.HashFile(target).Equals(ticket.CurrentHash, StringComparison.OrdinalIgnoreCase) ||
                !ReleaseTrust.HashFile(downloaded).Equals(ticket.NewHash, StringComparison.OrdinalIgnoreCase) ||
                after.Version != ticket.Version || Version.Parse(after.Version) <= Version.Parse(before.Version)) throw new IOException("Файлы обновления изменились.");
            using var parent = Process.GetProcessById(ticket.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != ticket.ParentStartTicks ||
                !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("Исходное приложение уже закрыто.");
            using var lease = new FileStream(Path.Combine(Root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var operation = Path.GetFileName(directory);
            staged = target + ".update-" + operation; backup = target + ".previous-" + operation;
            if (File.Exists(backup) || File.Exists(Path.Combine(directory, "started.json"))) throw new IOException("Папка обновления уже используется.");
            using (var input = File.OpenRead(downloaded))
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stagedCreated = true; input.CopyTo(output); output.Flush(true); }
            if (!ReleaseTrust.HashFile(staged).Equals(ticket.NewHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Файл обновления повреждён.");
            if (ReleaseTrust.VerifyExecutable(staged).Version != ticket.Version) throw new IOException("Подпись обновления не совпадает.");
            File.WriteAllText(Path.Combine(directory, "ready"), "1");
            var until = DateTime.UtcNow.AddSeconds(35);
            while (!File.Exists(Path.Combine(directory, "proceed")))
            {
                if (File.Exists(Path.Combine(directory, "cancel")) || parent.HasExited || DateTime.UtcNow > until) throw new IOException("Обновление отменено.");
                Thread.Sleep(100);
            }
            if (!parent.WaitForExit(45000)) throw new IOException("Закройте SneakyFit и повторите обновление.");
            parentExited = true;
            foreach (var running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                using (running)
                {
                    try { if (string.Equals(running.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("Закройте второе окно SneakyFit."); }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            if (IsLink(target) || !ReleaseTrust.HashFile(target).Equals(ticket.CurrentHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Установленная версия изменилась.");
            File.Replace(staged, target, backup, true); replaced = true;
            if (!ReleaseTrust.HashFile(target).Equals(ticket.NewHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Не удалось установить обновление.");
            var launch = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            launch.ArgumentList.Add("--update-started"); launch.ArgumentList.Add(work); launch.ArgumentList.Add(ticketHash);
            replacement = Process.Start(launch) ?? throw new IOException("Не удалось открыть новую версию.");
            var startedAt = replacement.StartTime.ToUniversalTime().Ticks;
            var startup = Stopwatch.StartNew();
            while (!StartupAcknowledged(directory, replacement.Id, startedAt, ticketHash))
            {
                if (replacement.HasExited) throw new IOException("Новая версия не запустилась. Восстанавливаю предыдущую.");
                if (startup.Elapsed > TimeSpan.FromSeconds(60))
                    throw new IOException("Не удалось подтвердить запуск новой версии. Предыдущая версия сохранена: " + backup);
                Thread.Sleep(100);
            }
            try { File.Delete(backup); backup = null; } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { File.WriteAllText(Path.Combine(directory, "completed"), "1"); } catch (Exception) { }
            return new(true, null);
        }
        catch (Exception failure)
        {
            var error = failure.Message;
            if (replaced && backup is not null && ticket is not null)
            {
                // Never replace an executable while its new process may still be running.
                if (CanRestore(replacement, ticket.Target))
                {
                    try { File.Replace(backup, ticket.Target, null, true); backup = null; error = "Новая версия не запустилась. Предыдущая версия восстановлена."; }
                    catch (Exception) { error = "Не удалось восстановить приложение. Копия: " + backup; }
                }
                else error = "Не удалось подтвердить запуск новой версии. Предыдущая версия сохранена: " + backup;
            }
            if (directory is not null)
                try { File.WriteAllText(Path.Combine(directory, "error.txt"), error); } catch (Exception) { }
            if (parentExited && ticket is not null)
            {
                try
                {
                    if (ReleaseTrust.HashFile(ticket.Target).Equals(ticket.CurrentHash, StringComparison.OrdinalIgnoreCase))
                        Process.Start(new ProcessStartInfo(ticket.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ticket.Target)! });
                }
                catch (Exception) { }
            }
            return new(parentExited, error);
        }
        finally
        {
            replacement?.Dispose();
            if (stagedCreated && staged is not null) try { File.Delete(staged); } catch (Exception) { }
        }
    }

    public static void AcknowledgeStartup(string work, string ticketHash)
    {
        work = Path.GetFullPath(work); var directory = Path.GetDirectoryName(work)!; ValidateDirectory(directory);
        if (!ReleaseTrust.IsHash(ticketHash) || Path.GetFileName(work) != "work.json" || new FileInfo(work).Length > 16 * 1024)
            throw new IOException("Некорректный запрос обновления.");
        var bytes = File.ReadAllBytes(work);
        if (bytes.Length > 16 * 1024 || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(ticketHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Запрос обновления изменился.");
        var ticket = JsonSerializer.Deserialize<UpdateTicket>(bytes) ?? throw new IOException("Запрос обновления повреждён.");
        if (!string.Equals(Environment.ProcessPath, ticket.Target, StringComparison.OrdinalIgnoreCase) ||
            !ReleaseTrust.IsHash(ticket.NewHash) || !ReleaseTrust.HashFile(ticket.Target).Equals(ticket.NewHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Запущен другой файл приложения.");
        using var process = Process.GetCurrentProcess();
        var receipt = new UpdateStartupReceipt(process.Id, process.StartTime.ToUniversalTime().Ticks, ticketHash);
        var temporary = Path.Combine(directory, "started.tmp");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, receipt); output.Flush(true);
        }
        File.Move(temporary, Path.Combine(directory, "started.json"));
    }

    private static bool StartupAcknowledged(string directory, int processId, long startTicks, string ticketHash)
    {
        var marker = Path.Combine(directory, "started.json");
        if (!File.Exists(marker)) return false;
        if (IsLink(marker) || new FileInfo(marker).Length > 4096) throw new IOException("Подтверждение запуска повреждено.");
        var receipt = JsonSerializer.Deserialize<UpdateStartupReceipt>(File.ReadAllBytes(marker));
        if (receipt?.ProcessId != processId || receipt.StartTicks != startTicks || receipt.TicketHash != ticketHash)
            throw new IOException("Подтверждение запуска не совпадает.");
        return true;
    }

    private static bool CanRestore(Process? replacement, string target)
    {
        try
        {
            if (replacement is not null && !replacement.HasExited) return false;
            foreach (var running in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                using (running)
                {
                    try { if (!running.HasExited && string.Equals(running.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) return false; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }
            }
            return true;
        }
        catch (Exception) { return false; }
    }

    private static async Task Copy(string source, string target, CancellationToken cancellation)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
    }
    private static string ReadError(string directory)
    {
        var file = Path.Combine(directory, "error.txt");
        return File.Exists(file) && new FileInfo(file).Length < 4096 ? File.ReadAllText(file) : "Не удалось подготовить обновление. Приложение осталось открытым.";
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void ValidateDirectory(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _) || Directory.Exists(full) && IsLink(full)) throw new IOException("Некорректная папка обновления.");
    }
}
