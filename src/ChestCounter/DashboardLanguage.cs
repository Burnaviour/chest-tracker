using System.Text.Json;

namespace ChestCounter;

/// <summary>Shares the dashboard's complete UTF-8 dictionaries with the native tray.</summary>
public static class DashboardLanguage
{
    public static readonly string[] Codes = { "id", "de", "en", "es", "es-419", "fr", "it", "pl", "pt-BR", "vi", "tr", "ru", "ar", "th", "ja", "zh-Hans", "zh-Hant", "ko" };
    private static Dictionary<string, string> _strings = new();
    public static string Code { get; private set; } = "en";
    public static bool Set(string? code)
    {
        var selected = Codes.FirstOrDefault(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (selected == null) return false;
        var asm = typeof(AppInfo).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".locales.{selected}.json", StringComparison.Ordinal));
        if (name == null) return false;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return false;
        _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        Code = selected;
        return true;
    }
    public static string Text(string key) => _strings.GetValueOrDefault(key, key);
}
