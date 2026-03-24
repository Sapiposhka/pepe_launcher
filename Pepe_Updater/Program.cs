using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.IO.Compression;

var baseDir = AppContext.BaseDirectory;
var cacheDirEarly = Path.Combine(baseDir, "cache");
Directory.CreateDirectory(cacheDirEarly);
var earlyLogPath = Path.Combine(cacheDirEarly, "update_log.txt");

try
{
    File.AppendAllText(earlyLogPath,
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] updater started. argsLength={args.Length}\n" +
        $"args=[{string.Join(" | ", args)}]\n");
}
catch
{
    // ignore
}

if (args.Length < 4)
{
    try
    {
        File.AppendAllText(earlyLogPath,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] invalid args count. Expected 4.\n");
    }
    catch
    {
        // ignore
    }

    Console.WriteLine("Usage: Pepe_Updater.exe <appDir> <updateZipPath> <launcherExePath> <latestVersion>");
    return 1;
}

var appDir = args[0];
var updateZipPath = args[1];
var launcherExePath = args[2];
var latestVersion = args[3];

if (!Directory.Exists(appDir))
{
    Console.WriteLine($"App directory not found: {appDir}");
    return 2;
}

if (!File.Exists(updateZipPath))
{
    Console.WriteLine($"Update zip not found: {updateZipPath}");
    return 3;
}

try
{
    var cacheDir = Path.Combine(appDir, "cache");
    Directory.CreateDirectory(cacheDir);
    var logPath = Path.Combine(cacheDir, "update_log.txt");

    File.AppendAllText(logPath,
        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] updater started latestVersion={latestVersion}\n" +
        $"launcherExePath={launcherExePath}\n" +
        $"updateZipPath={updateZipPath}\n");

    // Дополнительный маркер запуска (который проще проверить при отладке)
    try
    {
        var markerPath = Path.Combine(cacheDir, "update_started.marker");
        File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("o"));
    }
    catch
    {
        // ignore
    }

    // Ждём, пока лаунчер реально завершится (на слабых/медленных ПК может быть задержка закрытия).
    var launcherProcessName = Path.GetFileNameWithoutExtension(launcherExePath);
    for (var i = 0; i < 40; i++)
    {
        try
        {
            var exists = Process.GetProcessesByName(launcherProcessName).Length > 0;
            if (!exists) break;
        }
        catch
        {
            // в худшем случае продолжим, пусть копирование покажет ошибку
        }

        await Task.Delay(500);
    }

    var tempExtractDir = Path.Combine(Path.GetTempPath(), "PepeLauncherUpdate_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempExtractDir);

    // Базовая проверка, что файл реально является ZIP.
    try
    {
        using var _ = ZipFile.OpenRead(updateZipPath);
    }
    catch (Exception ex)
    {
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] zip open failed: {ex.Message}\n");
        return 20;
    }

    ZipFile.ExtractToDirectory(updateZipPath, tempExtractDir, overwriteFiles: true);

    // Определяем "корень" архива по расположению Pepe_Launcher.exe.
    // Это самый надёжный способ отрезать лишние уровни типа zipRoot/...
    var sourceRoot = tempExtractDir;
    try
    {
        var launcherExeName = Path.GetFileName(launcherExePath);
        var foundLauncher = Directory.EnumerateFiles(tempExtractDir, launcherExeName, SearchOption.AllDirectories)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(foundLauncher))
        {
            var parent = Path.GetDirectoryName(foundLauncher);
            if (!string.IsNullOrWhiteSpace(parent))
                sourceRoot = parent;
        }
    }
    catch
    {
        // fallback to tempExtractDir
    }

    var updaterFileName = Path.GetFileName(Environment.ProcessPath ?? "Pepe_Updater.exe");

    foreach (var srcFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceRoot, srcFile);
        var destination = Path.Combine(appDir, relative);

        var destinationDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        // Не пытаемся перезаписывать сам запущенный updater.
        if (string.Equals(Path.GetFileName(destination), updaterFileName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        // На некоторых учебных ПК файлы могут быть ещё “залочены” после закрытия UI.
        // Поэтому делаем несколько попыток.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Copy(srcFile, destination, overwrite: true);
                break;
            }
            catch (IOException)
            {
                if (attempt == 4) throw;
                Thread.Sleep(400);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == 4) throw;
                Thread.Sleep(400);
            }
        }
    }

    // Запускаем обновленный лаунчер.
    if (File.Exists(launcherExePath))
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = launcherExePath,
            WorkingDirectory = appDir,
            UseShellExecute = true
        });
    }

    // Обновляем локальную версию лаунчера, чтобы проверка обновлений работала корректно.
    try
    {
        var versionFile = Path.Combine(cacheDir, "launcher-version.json");
        var json = System.Text.Json.JsonSerializer.Serialize(new { version = latestVersion });
        File.WriteAllText(versionFile, json);
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] version file updated.\n");
    }
    catch (Exception ex)
    {
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] version update failed: {ex.Message}\n");
    }

    try
    {
        File.Delete(updateZipPath);
        Directory.Delete(tempExtractDir, recursive: true);
    }
    catch
    {
        // cleanup best effort
    }

    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] updater finished.\n");
    return 0;
}
catch (Exception ex)
{
    try
    {
        var cacheDir = Path.Combine(appDir, "cache");
        Directory.CreateDirectory(cacheDir);
        var logPath = Path.Combine(cacheDir, "update_log.txt");
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] updater crashed: {ex}\n");
    }
    catch
    {
        // ignore
    }
    Console.WriteLine(ex.ToString());
    return 10;
}

