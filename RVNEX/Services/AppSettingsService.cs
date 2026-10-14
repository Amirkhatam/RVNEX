using System;
using System.IO;
using System.Text.Json;

namespace RVNEX.Services;

internal sealed class AppSettingsService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RVNEX",
        "settings.json");

    public int CollectionTimeoutMinutes { get; private set; } = 30;
    public string Theme { get; private set; } = "System";
    public bool ConfirmBulkCollection { get; private set; }
    public int MaximumRetries { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var settings = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_path));
            if (settings?.SchemaVersion == 1 && settings.CollectionTimeoutMinutes is >= 5 and <= 240)
            {
                CollectionTimeoutMinutes = settings.CollectionTimeoutMinutes;
            }
            if (settings?.Theme is "System" or "Light" or "Dark")
            {
                Theme = settings.Theme;
            }
            ConfirmBulkCollection = settings?.ConfirmBulkCollection == true;
            if (settings?.MaximumRetries is >= 0 and <= 3) MaximumRetries = settings.MaximumRetries;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Settings are optional. Keep the safe in-memory default and preserve the file.
        }
    }

    public void SaveCollectionTimeout(int minutes)
    {
        if (minutes is < 5 or > 240) throw new ArgumentOutOfRangeException(nameof(minutes), "Timeout must be between 5 and 240 minutes.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        var value = new SettingsFile { CollectionTimeoutMinutes = minutes, Theme = Theme, ConfirmBulkCollection = ConfirmBulkCollection, MaximumRetries = MaximumRetries };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
            CollectionTimeoutMinutes = minutes;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void SaveConfirmBulkCollection(bool confirm)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        var value = new SettingsFile { CollectionTimeoutMinutes = CollectionTimeoutMinutes, Theme = Theme, ConfirmBulkCollection = confirm, MaximumRetries = MaximumRetries };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else File.Move(temporaryPath, _path);
            ConfirmBulkCollection = confirm;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public void SaveMaximumRetries(int retries)
    {
        if (retries is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(retries), "Retries must be between zero and three.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        var value = new SettingsFile { CollectionTimeoutMinutes = CollectionTimeoutMinutes, Theme = Theme, ConfirmBulkCollection = ConfirmBulkCollection, MaximumRetries = retries };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path)) { File.Copy(_path, _path + ".bak", true); File.Replace(temporaryPath, _path, null, true); }
            else File.Move(temporaryPath, _path);
            MaximumRetries = retries;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public void SaveTheme(string theme)
    {
        if (theme is not ("System" or "Light" or "Dark")) throw new ArgumentException("Choose System, Light, or Dark.", nameof(theme));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        var value = new SettingsFile { CollectionTimeoutMinutes = CollectionTimeoutMinutes, Theme = theme, ConfirmBulkCollection = ConfirmBulkCollection, MaximumRetries = MaximumRetries };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_path))
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
                File.Replace(temporaryPath, _path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
            Theme = theme;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed class SettingsFile
    {
        public int SchemaVersion { get; set; } = 1;
        public int CollectionTimeoutMinutes { get; set; } = 30;
        public string Theme { get; set; } = "System";
        public bool ConfirmBulkCollection { get; set; }
        public int MaximumRetries { get; set; }
    }
}
