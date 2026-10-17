using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampusConnect;

internal static class LocalStore
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusConnect");
    private static string FilePath => Path.Combine(DirectoryPath, "configuration.json");
    private static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private sealed record Configuration(CampusProfile Profile, string ProtectedPassword);
    public static (CampusProfile Profile, string Password) Load()
    {
        if (!File.Exists(FilePath)) return (new(), "");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("配置文件为空。");
        config.Profile.Validate();
        var password = string.IsNullOrEmpty(config.ProtectedPassword) ? "" : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(config.ProtectedPassword), null, DataProtectionScope.CurrentUser));
        return (config.Profile, password);
    }

    // Right after logon the DPAPI service can still be starting and decrypting the
    // password may block for seconds. Connecting and opening the login page do not need
    // it, so the profile comes back immediately and the password follows separately.
    public static (CampusProfile Profile, string? ProtectedPassword) LoadProfile()
    {
        if (!File.Exists(FilePath)) return (new(), "");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FilePath))
            ?? throw new InvalidDataException("配置文件为空。");
        config.Profile.Validate();
        return (config.Profile, string.IsNullOrEmpty(config.ProtectedPassword) ? null : config.ProtectedPassword);
    }

    public static string DecryptPassword(string protectedPassword) => string.IsNullOrEmpty(protectedPassword) ? "" : Encoding.UTF8.GetString(
        ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), null, DataProtectionScope.CurrentUser));

    // "Evergreen" (the default) honours WebView2 updates. "Fixed" pins the newest
    // installed runtime and turns its updater off, so a logon start never waits on one.
    public static string? ReadRuntimeChannel()
    {
        try {
            if (!File.Exists(SettingsPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var value = document.RootElement.TryGetProperty("WebView2Channel", out var channel) ? channel.GetString() : null;
            return value is "Fixed" or "Evergreen" ? value : null;
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteRuntimeChannel(string channel)
    {
        if (channel is not ("Fixed" or "Evergreen")) throw new ArgumentException("未知的浏览器运行方式。", nameof(channel));
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { WebView2Channel = channel },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Save(CampusProfile profile, string password)
    {
        profile.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var protectedPassword = string.IsNullOrEmpty(password) ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
        var temporary = Path.Combine(DirectoryPath, $"configuration-{Guid.NewGuid():N}.tmp");
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Configuration(profile, protectedPassword),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
