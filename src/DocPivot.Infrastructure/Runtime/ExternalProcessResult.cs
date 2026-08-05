namespace DocPivot.Infrastructure.Runtime;

public sealed record ExternalProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
