using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// Updates from the GitHub releases of Burnaviour/chest-tracker. The new .exe is only put in place when its size and
/// SHA-256 match what GitHub lists for the release, and it starts with "MZ" (a Windows program). The old one is kept
/// next to it as .old until the next start, so a failed swap can be rolled back.
/// </summary>
public static class Updater
{
    public const string Repo = "Burnaviour/chest-tracker";
    private const string AssetName = "BurnaviourChestTracker.exe";

    public sealed record Release(Version Version, string Tag, string Url, long Size, string Sha256, string? NotesUrl);

    /// <summary>The newest release found by the last check (null: none found yet).</summary>
    public static Release? Latest { get; private set; }
    public static DateTime? LastCheckUtc { get; private set; }
    public static string? LastError { get; private set; }
    /// <summary>"idle", "checking", "downloading", "restarting" or "failed".</summary>
    public static string State => Volatile.Read(ref _installing) == 1 ? _installState : Volatile.Read(ref _checking) == 1 ? "checking" : _failed ? "failed" : "idle";
    /// <summary>Called once a new version is in place: the app closes and starts it.</summary>
    public static Action? RequestRestart { get; set; }

    private static int _checking, _installing;
    private static volatile string _installState = "idle";
    private static volatile bool _failed;

    /// <summary>A test address for the release information (BCT_UPDATE_API). Unset: GitHub.</summary>
    private static readonly string? TestApi = Environment.GetEnvironmentVariable("BCT_UPDATE_API");
    private static string ApiUrl => TestApi ?? $"https://api.github.com/repos/{Repo}/releases/latest";

    public static Version Current => Normalize(AppInfo.Version);
    public static bool HasUpdate => Latest is { } r && r.Version > Current;
    public static bool Busy => Volatile.Read(ref _checking) == 1 || Volatile.Read(ref _installing) == 1;

    /// <summary>Only the downloaded single-file app can replace itself (not a build run from source).</summary>
    public static bool CanInstall => OperatingSystem.IsWindows() && string.IsNullOrEmpty(typeof(AppInfo).Assembly.Location) && AppInfo.ExePath != null;

    /// <summary>First check a minute after starting, then every 6 hours.</summary>
    public static bool CheckDue(DateTime startedUtc) => !Busy && (LastCheckUtc is { } last
        ? DateTime.UtcNow - last > TimeSpan.FromHours(6)
        : DateTime.UtcNow - startedUtc > TimeSpan.FromSeconds(45));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool Trusted(Uri? uri) => uri != null && (TestApi != null || uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)));

    private static HttpClient Client(TimeSpan timeout, bool binary = false)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BurnaviourChestTracker/{AppInfo.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
        return http;
    }

    /// <summary>Asks GitHub for the newest release. Never throws: problems end up in <see cref="LastError"/>.</summary>
    public static async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref _checking, 1, 0) != 0) return;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                LastError = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    ? "GitHub is busy. The app will try again later."
                    : $"GitHub answered {(int)response.StatusCode}.";
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            {
                LastError = null;
                return;
            }
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                LastError = "The newest release has no version number.";
                return;
            }
            var notes = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
            if (notes == null || !notes.StartsWith($"https://github.com/{Repo}/", StringComparison.OrdinalIgnoreCase)) notes = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (url == null || !Trusted(new Uri(url)))
                {
                    LastError = "The download address is not on GitHub.";
                    return;
                }
                if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    LastError = "The newest release has no checksum, so it cannot be checked.";
                    return;
                }
                Latest = new Release(Normalize(version), tag, url, asset.GetProperty("size").GetInt64(), digest[7..], notes);
                LastError = null;
                _failed = false;
                return;
            }
            LastError = "The newest release has no app to download.";
        }
        catch
        {
            LastError = "Could not reach GitHub. The app will try again later.";
        }
        finally
        {
            LastCheckUtc = DateTime.UtcNow;
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Downloads and installs <see cref="Latest"/> in the background, then asks the app to restart.</summary>
    public static bool InstallInBackground()
    {
        if (!HasUpdate || !CanInstall || Interlocked.CompareExchange(ref _installing, 1, 0) != 0) return false;
        _installState = "downloading";
        _failed = false;
        LastError = null;
        _ = Task.Run(async () =>
        {
            var error = await DownloadAndSwapAsync(Latest!);
            if (error == null)
            {
                _installState = "restarting";
                RequestRestart?.Invoke();
                return;
            }
            LastError = error;
            _failed = true;
            Volatile.Write(ref _installing, 0);
        });
        return true;
    }

    private static async Task<string?> DownloadAndSwapAsync(Release release)
    {
        var exe = AppInfo.ExePath!;
        var download = exe + ".download";
        try
        {
            using var http = Client(TimeSpan.FromMinutes(10), binary: true);
            using (var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                if (!Trusted(response.RequestMessage?.RequestUri)) throw new IOException("The download was sent somewhere other than GitHub.");
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(download);
                await source.CopyToAsync(target);
            }
            if (new FileInfo(download).Length != release.Size) throw new IOException("The download is incomplete.");
            await using (var file = File.OpenRead(download))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The download does not match its checksum.");
                file.Position = 0;
                if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new IOException("The download is not a Windows program.");
            }
        }
        catch (Exception ex)
        {
            TryDelete(download);
            return ex is HttpRequestException or TaskCanceledException ? "The download failed. Try again later." : ex.Message;
        }

        // Windows lets a running program be renamed, not deleted: the running one becomes .old
        var old = exe + ".old";
        if (File.Exists(old) && !TryDelete(old)) old = $"{exe}.{DateTime.UtcNow.Ticks}.old";
        try
        {
            File.Move(exe, old);
            try
            {
                File.Move(download, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(download);
            return "The new version could not be put in place: " + ex.Message;
        }
    }

    /// <summary>Removes what an earlier update left behind (the previous .exe, an unfinished download).</summary>
    public static void CleanupOld()
    {
        if (AppInfo.ExePath is not { } exe || Path.GetDirectoryName(exe) is not { } dir) return;
        try
        {
            foreach (var file in Directory.GetFiles(dir, Path.GetFileName(exe) + "*.old")) TryDelete(file);
        }
        catch { /* folder not readable: nothing to clean */ }
        TryDelete(exe + ".download");
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
