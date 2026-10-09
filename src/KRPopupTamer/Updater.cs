using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace KRPopupTamer;

// Finds a newer release on GitHub and installs it. The files at the root of the release zip replace this app's files:
// the running exe is renamed to *.old (Windows allows that), the new one takes its name, it is started with
// --after-update (it waits for this instance to exit), and the *.old files are deleted at the next start.
static class Updater
{
    const string Repository = "oguzhanf/kingdoms-reborn-popup-tamer", AssetName = "KRPopupTamer.zip", ExeName = "KRPopupTamer.exe";
    public const string AfterUpdateArgument = "--after-update";
    public static readonly Version Current = Normalize(Assembly.GetEntryAssembly()!.GetName().Version!);
    static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", $"KRPopupTamer/{Current}" } }, Timeout = TimeSpan.FromMinutes(2) };

    public sealed record Release(Version Version, string Page, string DownloadUrl, string? Sha256);

    // The latest release if it is newer than this app and has the app zip, otherwise null.
    public static async Task<Release?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var version)) return null;
        if (Normalize(version) <= Current) return null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:") ? s[7..] : null;
            return new Release(Normalize(version), root.GetProperty("html_url").GetString()!,
                asset.GetProperty("browser_download_url").GetString()!, digest);
        }
        return null;
    }

    // Downloads, checks and installs the release, then starts it. The caller exits the app afterwards.
    public static async Task InstallAsync(Release release)
    {
        var zipBytes = await Http.GetByteArrayAsync(release.DownloadUrl);
        if (release.Sha256 != null && !Convert.ToHexString(SHA256.HashData(zipBytes)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download does not match the checksum GitHub published for it.");
        var exePath = Environment.ProcessPath!;
        var folder = Path.GetDirectoryName(exePath)!;
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        var files = zip.Entries.Where(e => e.Name != "" && e.FullName == e.Name).ToList(); // files at the zip root
        if (!files.Any(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"The release zip has no {ExeName}.");
        // The zip's exe replaces this exe even if it was renamed; other files go next to it.
        string TargetOf(ZipArchiveEntry e) => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? exePath : Path.Combine(folder, e.Name);

        foreach (var file in files) file.ExtractToFile(TargetOf(file) + ".new", overwrite: true); // nothing replaced yet
        var swapped = new List<string>();
        try
        {
            foreach (var target in files.Select(TargetOf))
            {
                if (File.Exists(target)) File.Move(target, target + ".old", overwrite: true);
                swapped.Add(target);
                File.Move(target + ".new", target);
            }
        }
        catch
        {
            foreach (var target in swapped)
                try { if (File.Exists(target + ".old")) File.Move(target + ".old", target, overwrite: true); } catch (IOException) { }
            throw;
        }
        Process.Start(new ProcessStartInfo(exePath, AfterUpdateArgument) { UseShellExecute = false, WorkingDirectory = folder });
    }

    // Removes the files an update renamed to *.old; they are no longer in use at the next start.
    public static void DeleteLeftovers()
    {
        try
        {
            var exePath = Environment.ProcessPath!;
            var olds = Directory.EnumerateFiles(Path.GetDirectoryName(exePath)!, "KRPopupTamer*.old").Append(exePath + ".old");
            foreach (var old in olds.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
                try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
