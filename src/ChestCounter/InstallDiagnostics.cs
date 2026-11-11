using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ChestCounter;

/// <summary>Offline verification of the published EXE, without reading player data or installing anything.</summary>
public static class InstallDiagnostics
{
    public static string? WebViewVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return null; }
    }

    public static int Run(string[] args)
    {
        var assembly = typeof(AppInfo).Assembly;
        var resources = assembly.GetManifestResourceNames();
        var required = new[] { ".app.html", ".dashboard.html", ".app.js", ".ui.css", ".overlay.js", ".fonts.Fort-Black.woff2" };
        var missing = required.Where(file => !resources.Any(name => name.EndsWith(file, StringComparison.Ordinal))).ToArray();
        var locales = DashboardLanguage.Codes.All(code => resources.Any(name => name.EndsWith($".locales.{code}.json", StringComparison.Ordinal)));
        var runtime = WebViewVersion();
        var report = new {
            version = AppInfo.DisplayVersion,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            embeddedUi = missing.Length == 0, missingResources = missing,
            allLanguagesEmbedded = locales,
            webView2 = runtime,
            startupMode = runtime == null ? "browser-fallback" : "native-window",
            success = OperatingSystem.IsWindows() && missing.Length == 0 && locales
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var outputIndex = Array.IndexOf(args, "--diagnose-output");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length) File.WriteAllText(Path.GetFullPath(args[outputIndex + 1]), json);
        else Console.WriteLine(json);
        return report.success ? 0 : 1;
    }
}
