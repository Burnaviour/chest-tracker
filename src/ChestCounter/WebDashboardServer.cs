using System.Net;
using System.Text;
using System.Diagnostics;

namespace ChestCounter;

/// <summary>
/// Lightweight local HTTP server running at http://localhost:5088
/// Serves the live chest tracking dashboard, OBS Studio transparent overlay, CSV export, and JSON API.
/// </summary>
public static class WebDashboardServer
{
    private static HttpListener? _listener;
    private static Thread? _thread;
    public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("BCT_PORT"), out var port) && port is > 1024 and < 65536 ? port : 5088;
    public static string Url => $"http://localhost:{Port}/";
    public static string OverlayUrl => $"http://localhost:{Port}/overlay";
    public static bool IsListening => _listener != null && _listener.IsListening;
    public static int UiZoomPercent => Math.Clamp(_config?.UiZoomPercent ?? 100, 50, 150);

    /// <summary>Shows or hides the counter drawn over the game (set by Program).</summary>
    public static Action<bool>? SetGameOverlay { get; set; }

    private static Store? _store;
    private static Config? _config;
    private static TrackerData? _data;
    private static Func<bool>? _isLiveInProgress;
    private static Func<MatchRecord?>? _getLiveMatch;
    public static void Start(Store store, Config config, TrackerData data, Func<bool>? isLiveInProgress = null, Func<MatchRecord?>? getLiveMatch = null)
    {
        _store = store;
        _config = config;
        _data = data;
        DashboardLanguage.Set(config.UiLanguage ?? config.Language ?? "en");
        _isLiveInProgress = isLiveInProgress;
        _getLiveMatch = getLiveMatch;

        try
        {
            var logPath = Path.Combine(store.Folder, "trace.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [WebDashboardServer] Starting on http://localhost:{Port}/ and http://127.0.0.1:{Port}/ ...\r\n");

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [WebDashboardServer] Listener started successfully!\r\n");

            _thread = new Thread(() =>
            {
                while (_listener != null && _listener.IsListening)
                {
                    try
                    {
                        var context = _listener.GetContext();
                        ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                    }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { /* Continue */ }
                }
            })
            {
                IsBackground = true,
                Name = "WebDashboardServer"
            };
            _thread.Start();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  [Web Dashboard] Live at {Url}");
            Console.WriteLine($"  [OBS Overlay]   Live at {OverlayUrl}");
            Console.ResetColor();

        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(store.Folder, "trace.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [WebDashboardServer] ERROR: {ex}\r\n");
            }
            catch { }
            ErrorLog.Write("WebDashboardServer Start", ex);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [Web Dashboard] Notice: Local port {Port} unavailable ({ex.Message}). Will open HTML file directly.");
            Console.ResetColor();
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var req = context.Request;
            var res = context.Response;
            // Only this app's own pages may call the API: a request sent by another website is refused.
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && origin != $"http://localhost:{Port}" && origin != $"http://127.0.0.1:{Port}")
            { res.StatusCode = 403; res.Close(); return; }
            // Browsers say where a request comes from: links, images or scripts on another site are refused too.
            var site = req.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(site) && site != "same-origin" && site != "none")
            { res.StatusCode = 403; res.Close(); return; }
            if (!req.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && !req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            { res.StatusCode = 405; res.Close(); return; }

            var path = req.Url?.AbsolutePath ?? "/";

            if (path.Equals("/api/set-language", StringComparison.OrdinalIgnoreCase))
            {
                var code = req.QueryString["code"];
                if (!DashboardLanguage.Set(code)) { res.StatusCode = 400; res.Close(); return; }
                if (_config != null && _store != null) { _config.UiLanguage = DashboardLanguage.Code; _store.SaveConfig(_config); }
                MainGuiForm.CurrentInstance?.RefreshLanguage();
                res.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                res.OutputStream.Write(bytes); res.Close(); return;
            }

            // 1. Live Stats API (for OBS overlay and live updates)
            if (path.Equals("/api/live", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "application/json; charset=utf-8";
                var json = GetLiveStatsJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // 1b. Matches API (for Web Dashboard)
            if (path.Equals("/api/matches", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "application/json; charset=utf-8";
                var json = GetMatchesJson();
                var bytes = Encoding.UTF8.GetBytes(json);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // 2. Set Custom Base Count API
            if (path.Equals("/api/set-base", StringComparison.OrdinalIgnoreCase))
            {
                var countStr = req.QueryString["count"];
                if (!string.IsNullOrWhiteSpace(countStr) && countStr != "clear" && countStr != "reset" && countStr != "null"
                    && (!int.TryParse(countStr, out var validCount) || validCount < 0))
                { res.StatusCode = 400; res.Close(); return; }
                var targetProfile = req.QueryString["profile"] ?? req.QueryString["key"];
                if (_config != null && _store != null && _data != null)
                {
                    lock (_data)
                    {
                        var all = Profiles.List(_data);
                        Profile? target = null;
                        if (!string.IsNullOrWhiteSpace(targetProfile))
                        {
                            target = all.FirstOrDefault(p =>
                                p.Key.Equals(targetProfile, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(p.Name) && p.Name.Equals(targetProfile, StringComparison.OrdinalIgnoreCase)));
                        }
                        target ??= Profiles.Current(_data, _config);

                        var pKey = target?.Key ?? targetProfile?.Trim() ?? _config.Profile ?? "";
                        var pName = target?.Name ?? "";

                        if (string.IsNullOrWhiteSpace(countStr) ||
                            countStr.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                            countStr.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                            countStr.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                            countStr == "0")
                        {
                            if (!string.IsNullOrWhiteSpace(pKey)) _config.SetBaseChestForProfile(pKey, null);
                            if (!string.IsNullOrWhiteSpace(pName)) _config.SetBaseChestForProfile(pName, null);
                            _store.SaveConfig(_config);
                            ReportWriter.WriteAll(_store, _config, _data);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"  [Base Count Reset] Reverting to replay chest amount for profile: {pKey} / {pName}");
                            Console.ResetColor();
                        }
                        else if (int.TryParse(countStr, out var count) && count > 0)
                        {
                            if (!string.IsNullOrWhiteSpace(pKey)) _config.SetBaseChestForProfile(pKey, count);
                            if (!string.IsNullOrWhiteSpace(pName)) _config.SetBaseChestForProfile(pName, count);
                            var replayTotal = _data.Matches.Where(m => Profiles.Key(m).Equals(pKey, StringComparison.OrdinalIgnoreCase)).Sum(m => m.ChestsOpened);
                            if (!string.IsNullOrWhiteSpace(pKey)) _config.ProfileBaseReplayTotals[pKey] = replayTotal;
                            if (!string.IsNullOrWhiteSpace(pName)) _config.ProfileBaseReplayTotals[pName] = replayTotal;
                            var liveAtSave = (_isLiveInProgress?.Invoke() ?? false) ? _getLiveMatch?.Invoke() : null;
                            var liveBaseline = liveAtSave != null && (Profiles.Key(liveAtSave).Equals(pKey, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(liveAtSave.PlayerName, pName, StringComparison.OrdinalIgnoreCase)) ? liveAtSave.ChestsOpened : 0;
                            if (!string.IsNullOrWhiteSpace(pKey)) _config.ProfileBaseLiveTotals[pKey] = liveBaseline;
                            if (!string.IsNullOrWhiteSpace(pName)) _config.ProfileBaseLiveTotals[pName] = liveBaseline;
                            _store.SaveConfig(_config);
                            ReportWriter.WriteAll(_store, _config, _data);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"  [Base Count Saved] Custom starting chest count saved for {pKey} / {pName}: {count} (persisted in config.json)");
                            Console.ResetColor();
                        }
                    }
                }
                res.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // 2b. Set Active Account / Profile API
            if (path.Equals("/api/set-profile", StringComparison.OrdinalIgnoreCase))
            {
                var profileKey = req.QueryString["key"] ?? req.QueryString["profile"] ?? "";
                if (_config != null && _store != null && _data != null)
                {
                    lock (_data)
                    {
                        var all = Profiles.List(_data);
                        if (all.Count > 0)
                        {
                            var match = all.FirstOrDefault(p =>
                                p.Key.Equals(profileKey, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrEmpty(p.Name) && p.Name.Equals(profileKey, StringComparison.OrdinalIgnoreCase)));
                            _config.Profile = match != null ? match.Key : (string.IsNullOrWhiteSpace(profileKey) || profileKey.Equals("all", StringComparison.OrdinalIgnoreCase) ? all[0].Key : profileKey);
                        }
                        else
                        {
                            _config.Profile = profileKey;
                        }
                    }
                    _store.SaveConfig(_config);
                    ReportWriter.WriteAll(_store, _config, _data);
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"  [Account Switched] Active profile switched to: {_config.Profile}");
                    Console.ResetColor();
                }
                res.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // 2c. Get Settings API
            if (path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "application/json; charset=utf-8";
                bool isStartup = false;
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                        isStartup = rk?.GetValue("BurnaviourChestTracker") != null;
                    }
                    catch { }
                }

                var demosFolder = _config?.DemosFolder ?? "";
                int? baseCount = null;
                string activePlayer = "";
                if (_config != null && _data != null)
                {
                    lock (_data)
                    {
                        var cur = Profiles.Current(_data, _config);
                        activePlayer = cur?.Name ?? _config.Profile ?? "";
                        baseCount = _config.GetBaseChestForProfile(cur?.Key) ?? _config.GetBaseChestForProfile(cur?.Name) ?? _config.GetBaseChestForProfile(_config.Profile);
                    }
                }
                var baseStr = (baseCount.HasValue && baseCount.Value > 0) ? baseCount.Value.ToString() : "null";
                var startWithFn = _config?.StartWithFortnite ?? false;

                var overlayOptions = System.Text.Json.JsonSerializer.Serialize(_config?.OverlayOptions ?? new(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                var json = $"{{\"demosFolder\":\"{EscapeJson(demosFolder)}\",\"startWithWindows\":{(isStartup ? "true" : "false")},\"startWithFortnite\":{(startWithFn ? "true" : "false")},\"fortniteWindowMode\":{FortniteWindowMode()},\"gameOverlay\":{((_config?.ShowOverlay ?? false) && OperatingSystem.IsWindows() && NativeOverlay.IsVisible ? "true" : "false")},\"baseChestCount\":{baseStr},\"activeProfile\":\"{EscapeJson(activePlayer)}\",\"uiLanguage\":\"{EscapeJson(_config?.UiLanguage ?? _config?.Language ?? "en")}\",\"version\":\"{AppInfo.DisplayVersion}\",\"overlayOptions\":{overlayOptions},\"uiZoomPercent\":{UiZoomPercent}}}";
                var bytes = Encoding.UTF8.GetBytes(json);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            if (path.Equals("/api/set-overlay-options", StringComparison.OrdinalIgnoreCase))
            {
                if (req.HttpMethod != "POST") { res.StatusCode = 405; res.Close(); return; }
                if (_config == null || _store == null) { res.StatusCode = 503; res.Close(); return; }
                try
                {
                    if (req.ContentLength64 > 4096) { res.StatusCode = 413; res.Close(); return; }
                    var options = System.Text.Json.JsonSerializer.Deserialize<OverlayPreferences>(req.InputStream, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                    if (options == null || (options.Label?.Length ?? 0) > 64 || (options.Label?.Any(char.IsControl) ?? false))
                    { res.StatusCode = 400; res.Close(); return; }
                    options.Label ??= "";
                    _config.OverlayOptions = options; _store.SaveConfig(_config);
                    WriteJson(res, new { ok = true });
                }
                catch { res.StatusCode = 400; res.Close(); }
                return;
            }
            if (path.Equals("/api/set-zoom", StringComparison.OrdinalIgnoreCase))
            {
                if (_config == null || _store == null) { res.StatusCode = 503; res.Close(); return; }
                if (!int.TryParse(req.QueryString["percent"], out var percent) || percent < 50 || percent > 150)
                { res.StatusCode = 400; res.Close(); return; }
                _config.UiZoomPercent = percent;
                _store.SaveConfig(_config);
                MainGuiForm.CurrentInstance?.SetZoomPercent(percent);
                WriteJson(res, new { ok = true, uiZoomPercent = percent });
                return;
            }
            // 2d. Set Windows Startup API
            if (path.Equals("/api/set-startup", StringComparison.OrdinalIgnoreCase))
            {
                var enabledStr = req.QueryString["enabled"] ?? "false";
                var enabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase) || enabledStr == "1";
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                        if (rk != null)
                        {
                            if (enabled && AppInfo.ExePath != null)
                            {
                                rk.SetValue("BurnaviourChestTracker", $"\"{AppInfo.ExePath}\" --minimized");
                            }
                            else
                            {
                                rk.DeleteValue("BurnaviourChestTracker", false);
                            }
                        }
                    }
                    catch { res.StatusCode = 500; res.Close(); return; }
                }

                res.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // The counter drawn over the game: on / off
            if (path.Equals("/api/set-game-overlay", StringComparison.OrdinalIgnoreCase))
            {
                var on = (req.QueryString["enabled"] ?? "") is "true" or "1";
                if (SetGameOverlay == null) { res.StatusCode = 503; res.Close(); return; }
                SetGameOverlay(on);
                WriteJson(res, new { ok = true, gameOverlay = on });
                return;
            }

            // The counter over the game: what it is doing now (the shortcuts can change it at any time)
            if (path.Equals("/api/overlay-state", StringComparison.OrdinalIgnoreCase))
            {
                var windows = OperatingSystem.IsWindows();
                WriteJson(res, new
                {
                    visible = windows && NativeOverlay.IsVisible,
                    locked = windows && NativeOverlay.IsLocked,
                    lockShortcut = windows && NativeOverlay.LockHotkeyRegistered,
                    showShortcut = windows && NativeOverlay.HotkeyRegistered,
                });
                return;
            }
            if (path.Equals("/api/set-overlay-lock", StringComparison.OrdinalIgnoreCase))
            {
                var on = (req.QueryString["enabled"] ?? "") is "true" or "1";
                if (!OperatingSystem.IsWindows() || !NativeOverlay.IsRunning) { res.StatusCode = 409; res.Close(); return; }
                NativeOverlay.SetLocked(on);
                WriteJson(res, new { ok = true, locked = on });
                return;
            }

            // 2e. Set Custom Demos Folder API
            if (path.Equals("/api/set-demos-folder", StringComparison.OrdinalIgnoreCase))
            {
                var folder = req.QueryString["folder"];
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                { res.StatusCode = 400; res.Close(); return; }
                if (_config != null && _store != null && !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    _config.DemosFolder = folder;
                    _store.SaveConfig(_config);
                }

                res.ContentType = "application/json; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // 3. Serve Chest Image for OBS Overlay and Dashboard
            if (path.Equals("/assets/chest.png", StringComparison.OrdinalIgnoreCase) || 
                path.Equals("/assets/chest_fnv1.png", StringComparison.OrdinalIgnoreCase))
            {
                var candidatePaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "chest.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chest.png"),
                    Path.Combine(Directory.GetCurrentDirectory(), "ui", "assets", "chest.png"),
                };

                var assetPath = candidatePaths.FirstOrDefault(File.Exists);
                if (assetPath != null)
                {
                    res.ContentType = "image/png";
                    var bytes = File.ReadAllBytes(assetPath);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    res.Close();
                    return;
                }
            }

            // 4. Treasure Hunt data (chests by kind, treasure found, per match)
            if (path.Equals("/api/treasure", StringComparison.OrdinalIgnoreCase))
            {
                MatchRecord[] list = Array.Empty<MatchRecord>();
                if (_data != null && _config != null) lock (_data) { list = Profiles.View(_data, _config).Matches.ToArray(); }
                var live = (_isLiveInProgress?.Invoke() ?? false) ? _getLiveMatch?.Invoke() : null;
                var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(TreasureStats.Build(list, live), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                res.ContentType = "application/json; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body);
                res.Close();
                return;
            }

            // 5. Raw JSON Data API
            if (path.Equals("/api/data", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "application/json; charset=utf-8";
                if (_store != null && File.Exists(_store.DataPath))
                {
                    var bytes = File.ReadAllBytes(_store.DataPath);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes("{}");
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
                res.Close();
                return;
            }

            // 6. CSV Export
            if (path.Equals("/csv", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = "text/csv; charset=utf-8";
                res.Headers.Add("Content-Disposition", "attachment; filename=\"burnaviour-chests.csv\"");
                if (_store != null && File.Exists(_store.CsvPath))
                {
                    var bytes = File.ReadAllBytes(_store.CsvPath);
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                }
                res.Close();
                return;
            }

            // 7. Static Asset & Modern 1:2 Dashboard Router
            var cleanPath = path.TrimStart('/');
            if (cleanPath.Contains("..") || cleanPath.Contains('\\') || cleanPath.Contains(':'))
            { res.StatusCode = 400; res.Close(); return; }
            if (string.IsNullOrEmpty(cleanPath) || cleanPath.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                cleanPath = "app.html";
            }
            else if (cleanPath.Equals("overlay", StringComparison.OrdinalIgnoreCase) || cleanPath.Equals("obs", StringComparison.OrdinalIgnoreCase))
            {
                cleanPath = "overlay.html";
            }
            else if (cleanPath.Equals("dashboard", StringComparison.OrdinalIgnoreCase))
            {
                cleanPath = "app.html";
            }

            var staticCandidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", cleanPath),
                Path.Combine(Directory.GetCurrentDirectory(), "ui", cleanPath)
            };

            var resolvedFile = staticCandidates.FirstOrDefault(File.Exists);
            if (resolvedFile != null)
            {
                var ext = Path.GetExtension(resolvedFile).ToLowerInvariant();
                res.ContentType = ext switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    ".js" or ".mjs" => "application/javascript; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png" => "image/png",
                    ".svg" => "image/svg+xml",
                    ".ico" => "image/x-icon",
                    ".woff2" => "font/woff2",
                    ".ttf" => "font/ttf",
                    _ => "application/octet-stream"
                };

                var bytes = File.ReadAllBytes(resolvedFile);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
                return;
            }

            // Embedded Resources Fallback: Enables 100% standalone single-file .exe operation on ANY new PC with zero external folders
            var asm = typeof(AppInfo).Assembly;
            var resNames = asm.GetManifestResourceNames();
            var targetSuffix = cleanPath.Replace('/', '.').Replace('\\', '.');
            var matchedRes = resNames.FirstOrDefault(r =>
                r.EndsWith("." + targetSuffix, StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith("." + Path.GetFileName(cleanPath), StringComparison.OrdinalIgnoreCase));

            if (matchedRes != null)
            {
                using var stream = asm.GetManifestResourceStream(matchedRes);
                if (stream != null)
                {
                    var ext = Path.GetExtension(cleanPath).ToLowerInvariant();
                    res.ContentType = ext switch
                    {
                        ".html" => "text/html; charset=utf-8",
                        ".css" => "text/css; charset=utf-8",
                        ".js" or ".mjs" => "application/javascript; charset=utf-8",
                        ".json" => "application/json; charset=utf-8",
                        ".png" => "image/png",
                        ".svg" => "image/svg+xml",
                        ".ico" => "image/x-icon",
                        ".woff2" => "font/woff2",
                        ".ttf" => "font/ttf",
                        _ => "application/octet-stream"
                    };

                    res.ContentLength64 = stream.Length;
                    stream.CopyTo(res.OutputStream);
                    res.Close();
                    return;
                }
            }

            // Fallback to legacy report if static index not found
            res.ContentType = "text/html; charset=utf-8";
            if (_store != null && File.Exists(_store.ReportPath))
            {
                var bytes = File.ReadAllBytes(_store.ReportPath);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                var html = "<html><body style='font-family:sans-serif;background:#0d0f14;color:#fff;padding:40px;text-align:center;'>"
                         + "<h1 style='color:#f5a623;'>Burnaviour Chest Tracker</h1>"
                         + "<p>Analyzing replays and building initial dashboard... Please wait.</p>"
                         + "<meta http-equiv='refresh' content='2'></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
            }
            res.Close();
        }
        catch
        {
            // Socket closed or connection aborted
        }
    }

    private static string GetSnapshotJson(string targetRoom)
    {
        if (_data == null || _config == null || _data.Matches.Count == 0)
        {
            return $"{{\"room\":\"{EscapeJson(targetRoom)}\",\"total\":0,\"lastMatch\":0,\"liveChests\":0,\"base\":null,\"session\":0,\"player\":\"\",\"accounts\":[],\"matches\":[],\"inProgress\":false}}";
        }

        try
        {
            MatchRecord[] matchArray;
            string playerName = "";
            string profileKey = "";
            Profile? cur;
            lock (_data)
            {
                cur = Profiles.Current(_data, _config);
                if (cur != null)
                {
                    playerName = cur.Name;
                    profileKey = cur.Key;
                }
                else
                {
                    playerName = !string.IsNullOrWhiteSpace(_config.Profile) ? _config.Profile : "";
                    profileKey = playerName;
                }
                var view = Profiles.View(_data, _config);
                matchArray = view.Matches.ToArray();
            }

            // Per-profile starting chest count (strictly isolated between accounts)
            var baseCount = _config.GetBaseChestForProfile(profileKey) ?? _config.GetBaseChestForProfile(playerName);
            var matches = matchArray.OrderByDescending(ReportWriter.When).ToList();
            var inProgress = _isLiveInProgress?.Invoke() ?? false;
            var liveMatch = inProgress ? _getLiveMatch?.Invoke() : null;
            var liveChests = liveMatch != null && (Profiles.Key(liveMatch).Equals(profileKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(liveMatch.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)) ? liveMatch.ChestsOpened : 0;
            var sessionChests = matches.Where(ReportWriter.IsThisSession).Sum(m => m.ChestsOpened) + liveChests;
            var totalChests = matches.Sum(m => m.ChestsOpened) + liveChests;
            int displayedTotal;
            lock (_data)
            {
                var baselinesBefore = _config.ProfileBaseReplayTotals.Count;
                displayedTotal = _config.DisplayedChestTotal(profileKey, playerName, totalChests - liveChests, liveChests);
                if (_config.ProfileBaseReplayTotals.Count != baselinesBefore) _store?.SaveConfig(_config);
            }
            var lastMatch = matches.Count > 0 ? matches[0].ChestsOpened : 0;
            var baseJson = (baseCount.HasValue && baseCount.Value > 0) ? baseCount.Value.ToString() : "null";

            // Format accounts list (ordered by last played descending)
            var profileItems = Profiles.List(_data).Select(p =>
            {
                var isActive = cur != null && (string.Equals(p.Key, cur.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, cur.Name, StringComparison.OrdinalIgnoreCase));
                return $"{{\"key\":\"{EscapeJson(p.Key)}\",\"name\":\"{EscapeJson(p.Name)}\",\"matches\":{p.Matches},\"chests\":{p.Chests},\"active\":{(isActive ? "true" : "false")}}}";
            });
            var accountsJson = "[" + string.Join(",", profileItems) + "]";
            var matchesJson = GetMatchesJson();

            return $"{{\"room\":\"{EscapeJson(targetRoom)}\",\"total\":{displayedTotal},\"lastMatch\":{lastMatch},\"liveChests\":{liveChests},\"base\":{baseJson},\"session\":{sessionChests},\"player\":\"{EscapeJson(playerName)}\",\"accounts\":{accountsJson},\"matches\":{matchesJson},\"inProgress\":{(inProgress ? "true" : "false")}}}";
        }
        catch
        {
            return $"{{\"room\":\"{EscapeJson(targetRoom)}\",\"total\":0,\"lastMatch\":0,\"liveChests\":0,\"base\":null,\"session\":0,\"player\":\"\",\"accounts\":[],\"matches\":[],\"inProgress\":false}}";
        }
    }

    private static string GetLiveStatsJson()
    {
        string curName = "";
        if (_config != null && _data != null)
        {
            lock (_data)
            {
                var cur = Profiles.Current(_data, _config);
                if (cur != null && !string.IsNullOrWhiteSpace(cur.Name))
                {
                    curName = cur.Name;
                }
            }
        }
        var snapshot = GetSnapshotJson(!string.IsNullOrWhiteSpace(curName) ? curName : "default");
        var options = System.Text.Json.JsonSerializer.Serialize(_config?.OverlayOptions ?? new(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return snapshot[..^1] + ",\"uiLanguage\":\"" + EscapeJson(_config?.UiLanguage ?? _config?.Language ?? "en") + "\",\"overlayOptions\":" + options + "}";
    }

    private static string GetMatchesJson()
    {
        if (_data == null || _config == null || _data.Matches.Count == 0) return "[]";
        try
        {
            MatchRecord[] matchArray;
            string curProfileName = "";
            lock (_data)
            {
                var cur = Profiles.Current(_data, _config);
                if (cur != null && !string.IsNullOrWhiteSpace(cur.Name))
                {
                    curProfileName = cur.Name;
                }
                var view = Profiles.View(_data, _config);
                matchArray = view.Matches.ToArray();
            }
            var items = matchArray.OrderByDescending(ReportWriter.When).Take(150).Select(m =>
            {
                var fn = Path.GetFileName(m.ReplayFile ?? "");
                var start = ReportWriter.When(m).ToString("o");
                var pl = m.Playlist ?? "Battle Royale";
                var place = m.Placement.HasValue ? m.Placement.Value.ToString() : "null";
                var elim = m.Eliminations.HasValue ? m.Eliminations.Value.ToString() : "0";
                var player = EscapeJson(!string.IsNullOrEmpty(m.PlayerName) ? m.PlayerName : curProfileName);
                return $"{{\"fileName\":\"{EscapeJson(fn)}\",\"matchStart\":\"{start}\",\"durationSeconds\":{m.DurationSeconds ?? 0},\"playlist\":\"{EscapeJson(pl)}\",\"placement\":{place},\"chestsOpened\":{m.ChestsOpened},\"eliminations\":{elim},\"playerName\":\"{player}\"}}";
            });
            return "[" + string.Join(",", items) + "]";
        }
        catch { return "[]"; }
    }

    private static void WriteJson(HttpListenerResponse response, object value)
    {
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }

    /// <summary>Fortnite's saved window mode: 0 exclusive fullscreen, 1 windowed fullscreen, 2 windowed; -1 unknown.</summary>
    private static int FortniteWindowMode()
    {
        try
        {
            var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Config", "WindowsClient", "GameUserSettings.ini");
            foreach (var line in File.ReadLines(ini))
            {
                if (line.StartsWith("FullscreenMode=", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), out var mode)) return mode;
            }
        }
        catch { /* no settings file: unknown */ }
        return -1;
    }

    private static string EscapeJson(string s) => System.Text.Json.JsonSerializer.Serialize(s ?? "")[1..^1];

    public static void OpenInBrowser(string fallbackFilePath)
    {
        var target = IsListening ? Url : fallbackFilePath;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            Console.WriteLine($"  Open this link in your browser: {target}");
        }
    }

    public static void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
    }
}
