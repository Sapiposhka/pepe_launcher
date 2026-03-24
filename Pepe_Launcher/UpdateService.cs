using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Pepe_Launcher;

public class LauncherManifest
{
    public string LatestVersion { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
}

public class UpdateCheckResult
{
    public bool HasUpdate { get; init; }
    public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);
    public Version? LatestVersion { get; init; }
    public LauncherManifest? Manifest { get; init; }
}

public static class UpdateService
{
    // Публичная ссылка на launcher-manifest.json (disk.yandex.ru/d/...)
    private const string LauncherManifestPublicUrl = "https://disk.yandex.ru/d/qm_LJch1bPrHfQ";

    private static readonly HttpClient Http = new();
    private static readonly string CacheManifestFileName = "launcher-manifest.json";
    private static readonly string CacheVersionFileName = "launcher-version.json";

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var currentVersion = GetCurrentVersion();

        LauncherManifest manifest;
        try
        {
            manifest = await LoadManifestRemoteAsync();

            // Если манифест новее локального — обновляем cache даже до установки.
            TryLoadManifestFromCache(out var cachedLatestVersion);
            if (cachedLatestVersion is null)
            {
                SaveManifestToCache(manifest);
            }
            else if (TryParseVersion(manifest.LatestVersion, out var remoteLatest) &&
                     cachedLatestVersion is not null &&
                     remoteLatest > cachedLatestVersion)
            {
                SaveManifestToCache(manifest);
            }
        }
        catch
        {
            // Без интернета — работаем от локального cache
            manifest = LoadManifestFromCache();
        }

        if (!Version.TryParse(manifest.LatestVersion, out var latestVersion))
        {
            throw new InvalidOperationException("В launcher-manifest.json поле latestVersion имеет неверный формат.");
        }

        return new UpdateCheckResult
        {
            HasUpdate = latestVersion > currentVersion,
            CurrentVersion = currentVersion,
            LatestVersion = latestVersion,
            Manifest = manifest
        };
    }

    public static async Task<string> DownloadUpdateAsync(LauncherManifest manifest, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            throw new InvalidOperationException("В launcher-manifest.json отсутствует downloadUrl.");
        }

        var updatesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
        Directory.CreateDirectory(updatesDir);

        var latestVersion = string.IsNullOrWhiteSpace(manifest.LatestVersion) ? "unknown" : manifest.LatestVersion;
        var updateZipPath = Path.Combine(updatesDir, $"PepeLauncher_{latestVersion}.zip");

        var directUrl = await ResolveYandexPublicUrlAsync(manifest.DownloadUrl);
        await DownloadFileWithProgressAsync(directUrl, updateZipPath, progress);

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var actual = ComputeSha256(updateZipPath);
            var expected = manifest.Sha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA256 не совпадает. Ожидалось: {expected}, получено: {actual}");
            }
        }

        return updateZipPath;
    }

    public static void LaunchUpdaterAndExit(string updateZipPath, string latestVersion)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var normalizedBaseDir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updaterPath = Path.Combine(baseDir, "Pepe_Updater.exe");
        var launcherPath = Path.Combine(baseDir, "Pepe_Launcher.exe");

        // Маркер, что лаунчер решил обновляться и что именно скачал.
        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var pendingPath = Path.Combine(cacheDir, "update_pending.json");
            var json = JsonSerializer.Serialize(new
            {
                latestVersion,
                updateZipPath,
                startedAt = DateTimeOffset.UtcNow.ToString("o")
            });
            File.WriteAllText(pendingPath, json);
        }
        catch
        {
            // best-effort
        }

        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("Не найден Pepe_Updater.exe рядом с лаунчером.", updaterPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var logPath = Path.Combine(cacheDir, "update_log.txt");

            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] starting updater.\n" +
                $"updaterPath={updaterPath}\n" +
                $"launcherPath={launcherPath}\n" +
                $"updateZipPath={updateZipPath}\n" +
                $"args=[{normalizedBaseDir} | {updateZipPath} | {launcherPath} | {latestVersion}]\n");

            // Передаём аргументы безопасно (без проблем с кавычками/слэшами).
            psi.ArgumentList.Add(normalizedBaseDir);
            psi.ArgumentList.Add(updateZipPath);
            psi.ArgumentList.Add(launcherPath);
            psi.ArgumentList.Add(latestVersion);

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            var cacheDir = Path.Combine(baseDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var logPath = Path.Combine(cacheDir, "update_log.txt");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Process.Start failed: {ex}\n");
            throw;
        }

        Environment.Exit(0);
    }

    private static async Task<LauncherManifest> LoadManifestRemoteAsync()
    {
        var manifestDirectUrl = await ResolveYandexPublicUrlAsync(LauncherManifestPublicUrl);
        var json = await Http.GetStringAsync(manifestDirectUrl);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, options);
        if (manifest is null)
        {
            throw new InvalidOperationException("Не удалось прочитать launcher-manifest.json.");
        }

        return manifest;
    }

    private static Version GetCurrentVersion()
    {
        // Главный источник правды: кэш-файл, который пишет updater после успешной установки.
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        var versionFile = Path.Combine(cacheDir, CacheVersionFileName);

        if (File.Exists(versionFile))
        {
            try
            {
                var json = File.ReadAllText(versionFile);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v))
                {
                    var versionStr = v.GetString() ?? string.Empty;
                    if (TryParseVersion(versionStr, out var parsed))
                        return parsed;
                }
            }
            catch
            {
                // fallback ниже
            }
        }

        // fallback: версия сборки
        return typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private static bool TryParseVersion(string versionStr, out Version version)
    {
        if (string.IsNullOrWhiteSpace(versionStr))
        {
            version = new Version(0, 0, 0, 0);
            return false;
        }

        return Version.TryParse(versionStr.Trim(), out version!);
    }

    private static string CacheDirPath()
        => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

    private static void SaveManifestToCache(LauncherManifest manifest)
    {
        Directory.CreateDirectory(CacheDirPath());
        var path = Path.Combine(CacheDirPath(), CacheManifestFileName);
        var json = JsonSerializer.Serialize(manifest);
        File.WriteAllText(path, json);
    }

    private static LauncherManifest LoadManifestFromCache()
    {
        var path = Path.Combine(CacheDirPath(), CacheManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Нет интернета и отсутствует локальный launcher-manifest.json в cache.");
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, options);
        if (manifest is null)
            throw new InvalidOperationException("Не удалось прочитать локальный launcher-manifest.json.");

        return manifest;
    }

    private static LauncherManifest? TryLoadManifestFromCache(out Version? latestVersion)
    {
        latestVersion = null;
        var path = Path.Combine(CacheDirPath(), CacheManifestFileName);
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<LauncherManifest>(json, options);
        if (manifest is null)
            return null;

        if (TryParseVersion(manifest.LatestVersion, out var v))
            latestVersion = v;

        return manifest;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ResolveYandexPublicUrlAsync(string publicUrl)
    {
        if (!publicUrl.Contains("disk.yandex.ru/", StringComparison.OrdinalIgnoreCase))
        {
            return publicUrl;
        }

        var apiUrl =
            $"https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key={Uri.EscapeDataString(publicUrl)}";

        var json = await Http.GetStringAsync(apiUrl);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("href", out var hrefProp))
        {
            return hrefProp.GetString()
                   ?? throw new InvalidOperationException("Не удалось получить href из ответа Яндекс Диска.");
        }

        throw new InvalidOperationException("Ответ Яндекс Диска не содержит поля href.");
    }

    private static async Task DownloadFileWithProgressAsync(string url, string destinationFilePath, IProgress<double>? progress)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        progress?.Report(0);

        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await output.WriteAsync(buffer, 0, read);
            totalRead += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = (double)totalRead / totalBytes.Value * 100.0;
                progress?.Report(Math.Clamp(percent, 0, 100));
            }
        }

        progress?.Report(100);
    }
}

