using System.Text.Json;

namespace DocPivot.Infrastructure.Preferences;

public sealed class JsonUserPreferencesStore(string filePath) : IUserPreferencesStore
{
    public static JsonUserPreferencesStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DocPivot", "preferences.json"));

    public UserPreferences Load()
    {
        try
        {
            return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(filePath)) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(UserPreferences preferences)
    {
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences));
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences must not interrupt document processing.
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
