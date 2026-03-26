using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Pepe_Launcher;

public class GameList
{
    public List<GameItem> Games { get; set; } = new();
}

public class GameItem
{
    public string Name { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public List<string> DownloadUrls { get; set; } = new();
    public string ExePath { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;

    public string InstallFolder =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "games", Name);

    public string ExeFullPath =>
        Path.Combine(InstallFolder, ExePath);

    public bool IsInstalled
    {
        get
        {
            // Быстро проверяем ожидаемый путь, а если не нашли —
            // пытаемся найти exe внутри распакованной папки.
            GameService.ResolveExePathIfMissing(this);
            return File.Exists(ExeFullPath);
        }
    }
}

public static class GameService
{
    // Публичная ссылка (disk.yandex.ru/d/...) на spisok.json
    private const string GameListPublicUrl = "https://disk.yandex.ru/d/Xues-OFVC0luIw";

    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, string> ResolvedExePaths = new();

    public static async Task<GameList> LoadGameListAsync()
    {
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        Directory.CreateDirectory(cacheDir);
        var cacheFile = Path.Combine(cacheDir, "spisok.json");

        string? json = null;

        try
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "spisok.json");

            // Получаем прямой href через публичное API Яндекс Диска
            var listDirectUrl = await ResolveYandexPublicUrlAsync(GameListPublicUrl);
            var bytes = await Http.GetByteArrayAsync(listDirectUrl);
            await File.WriteAllBytesAsync(tempFile, bytes);
            await File.WriteAllBytesAsync(cacheFile, bytes);

            json = await File.ReadAllTextAsync(tempFile);
        }
        catch
        {
            // Нет интернета/упал запрос — работаем автономно из cache
            if (File.Exists(cacheFile))
            {
                json = await File.ReadAllTextAsync(cacheFile);
            }
            else
            {
                throw;
            }
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var gameList = JsonSerializer.Deserialize<GameList>(json, options)
                       ?? new GameList();

        return gameList;
    }

    public static async Task InstallGameAsync(GameItem game, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(game.InstallFolder);

        string tempZip = Path.Combine(Path.GetTempPath(), $"{game.Name}.zip");
        try
        {
            var sourceUrls = GetSourceUrls(game);
            if (sourceUrls.Count == 0)
            {
                throw new InvalidOperationException($"У игры \"{game.Name}\" не указан downloadUrl/downloadUrls.");
            }

            // Старый формат: один URL -> как раньше.
            if (sourceUrls.Count == 1)
            {
                var directUrl = await ResolveYandexPublicUrlAsync(sourceUrls[0]);
                await DownloadFileWithProgressAsync(directUrl, tempZip, progress, cancellationToken);
            }
            else
            {
                // Multipart: качаем части и склеиваем в единый ZIP.
                var partFiles = new List<string>();
                try
                {
                    for (var i = 0; i < sourceUrls.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var partTemp = Path.Combine(Path.GetTempPath(), $"{game.Name}.part{i + 1:000}");
                        partFiles.Add(partTemp);

                        var directUrl = await ResolveYandexPublicUrlAsync(sourceUrls[i]);
                        var index = i;
                        var partProgress = new Progress<double>(p =>
                        {
                            // Грубый общий прогресс: вклад каждой части одинаковый.
                            var combined = ((index * 100.0) + p) / sourceUrls.Count;
                            progress?.Report(Math.Clamp(combined, 0, 100));
                        });
                        await DownloadFileWithProgressAsync(directUrl, partTemp, partProgress, cancellationToken);
                    }

                    await MergePartsIntoZipAsync(partFiles, tempZip, cancellationToken);
                    progress?.Report(100);
                }
                finally
                {
                    foreach (var part in partFiles)
                    {
                        if (File.Exists(part))
                        {
                            try { File.Delete(part); } catch { /* ignore */ }
                        }
                    }
                }
            }

            if (!Directory.Exists(game.InstallFolder))
            {
                Directory.CreateDirectory(game.InstallFolder);
            }

            cancellationToken.ThrowIfCancellationRequested();
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, game.InstallFolder, true);
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { /* ignore */ }
            }
        }
    }

    private static List<string> GetSourceUrls(GameItem game)
    {
        if (game.DownloadUrls is { Count: > 0 })
        {
            return game.DownloadUrls
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(game.DownloadUrl))
        {
            return new List<string> { game.DownloadUrl.Trim() };
        }

        return new List<string>();
    }

    private static async Task MergePartsIntoZipAsync(List<string> partFiles, string outputZipPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (var part in partFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
            await input.CopyToAsync(output, 81920, cancellationToken);
        }
    }

    public static void DeleteGame(GameItem game)
    {
        if (Directory.Exists(game.InstallFolder))
        {
            Directory.Delete(game.InstallFolder, recursive: true);
        }
    }

    // Если exe лежит не ровно по указанному exePath (часто из-за лишней корневой папки в zip),
    // ищем его по имени внутри install folder и подменяем ExePath в объекте.
    public static void ResolveExePathIfMissing(GameItem game)
    {
        try
        {
            if (File.Exists(game.ExeFullPath))
                return;

            var exeFileName = Path.GetFileName(game.ExePath);
            if (string.IsNullOrWhiteSpace(exeFileName))
                return;

            var key = game.InstallFolder + "|" + exeFileName;
            if (ResolvedExePaths.TryGetValue(key, out var resolvedFullPath))
            {
                if (File.Exists(resolvedFullPath))
                {
                    // переводим full path обратно в относительный exePath
                    game.ExePath = Path.GetRelativePath(game.InstallFolder, resolvedFullPath)
                        .Replace('/', '\\');
                    return;
                }
            }

            if (!Directory.Exists(game.InstallFolder))
                return;

            var found = Directory.EnumerateFiles(game.InstallFolder, exeFileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(found) || !File.Exists(found))
                return;

            ResolvedExePaths[key] = found;
            game.ExePath = Path.GetRelativePath(game.InstallFolder, found).Replace('/', '\\');
        }
        catch
        {
            // игнорируем любые ошибки поиска: в худшем случае игра останется "не установленной"
        }
    }

    public static async Task<string?> GetOrDownloadPreviewImagePathAsync(GameItem game)
    {
        if (string.IsNullOrWhiteSpace(game.ImageUrl))
            return null;

        // Если это НЕ Яндекс Диск, просто вернём URL (WPF сам подгрузит по http/https)
        if (!game.ImageUrl.Contains("disk.yandex.ru/", StringComparison.OrdinalIgnoreCase))
            return game.ImageUrl;

        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "previews");
        Directory.CreateDirectory(cacheDir);

        var safeName = MakeSafeFileName(game.Name);
        var urlHash = StableHexHash(game.ImageUrl);

        // Сначала узнаем прямую ссылку и тип контента, чтобы выбрать расширение
        var directUrl = await ResolveYandexPublicUrlAsync(game.ImageUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, directUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var ext = contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".img"
        };

        var cachePath = Path.Combine(cacheDir, $"{safeName}_{urlHash}{ext}");
        if (File.Exists(cachePath))
            return cachePath;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await input.CopyToAsync(output);

        return cachePath;
    }

    public static void RunGame(GameItem game)
    {
        ResolveExePathIfMissing(game);
        if (!File.Exists(game.ExeFullPath))
        {
            MessageBox.Show($"Файл {game.ExeFullPath} не найден.", "Ошибка", MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = game.ExeFullPath,
            WorkingDirectory = Path.GetDirectoryName(game.ExeFullPath) ?? game.InstallFolder,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    private static async Task<string> ResolveYandexPublicUrlAsync(string publicUrl)
    {
        // Официальное публичное API: вернёт JSON с полем "href"
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

    private static async Task DownloadFileWithProgressAsync(string url, string destinationFilePath, IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        progress?.Report(0);

        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = (double)totalRead / totalBytes.Value * 100.0;
                progress?.Report(Math.Clamp(percent, 0, 100));
            }
        }

        progress?.Report(100);
    }

    private static string MakeSafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "game" : name;
    }

    private static string StableHexHash(string input)
    {
        unchecked
        {
            // лёгкий стабильный хеш (FNV-1a 32-bit)
            const uint fnvOffset = 2166136261;
            const uint fnvPrime = 16777619;
            uint hash = fnvOffset;
            for (int i = 0; i < input.Length; i++)
            {
                hash ^= input[i];
                hash *= fnvPrime;
            }
            return hash.ToString("x8");
        }
    }
}

