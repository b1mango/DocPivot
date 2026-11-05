namespace DocPivot.Infrastructure.Preferences;

public sealed record UserPreferences(string? Theme = null, string? LastOutputDirectory = null,
    IReadOnlyList<RecentOutput>? RecentOutputs = null);

public interface IUserPreferencesStore
{
    UserPreferences Load();
    void Save(UserPreferences preferences);
}
