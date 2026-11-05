namespace DocPivot.Infrastructure.Preferences;

public sealed record RecentOutput(string Path, string DisplayName, DateTimeOffset CreatedAt, bool IsFolder = false);
