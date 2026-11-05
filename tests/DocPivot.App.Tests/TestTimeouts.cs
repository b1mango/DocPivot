namespace DocPivot.App.Tests;

internal static class TestTimeouts
{
    /// <summary>
    /// Upper bound for "this signal should already have fired". Generous because CI
    /// runners have two cores shared by parallel test classes that spawn processes.
    /// </summary>
    public static readonly TimeSpan Signal = TimeSpan.FromSeconds(10);
}
