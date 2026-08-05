namespace DocPivot.Infrastructure.Runtime;

public sealed record ExternalToolAvailability(string Name, bool IsAvailable, string? Path);

