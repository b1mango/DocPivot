using DocPivot.Infrastructure.Preferences;

namespace DocPivot.Infrastructure.Tests.Preferences;

public sealed class JsonUserPreferencesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null)]
    [InlineData("broken")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Theme\":42}")]
    public void Load_MissingOrInvalidDataReturnsDefaults(string? json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "preferences.json");
        if (json is not null) File.WriteAllText(path, json);
        Assert.Equal(new UserPreferences(), new JsonUserPreferencesStore(path).Load());
    }

    [Fact]
    public void Save_CreatesDirectoryAndReplacesExistingPreferences()
    {
        var store = new JsonUserPreferencesStore(Path.Combine(_root, "preferences.json"));
        store.Save(new("Dark", "first"));
        store.Save(new("Light", "second"));
        Assert.Equal(new UserPreferences("Light", "second"), store.Load());
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Save_IoFailureDoesNotThrowOrLeaveTemporaryFiles()
    {
        Directory.CreateDirectory(_root);
        var store = new JsonUserPreferencesStore(_root);
        store.Save(new("Dark"));
        Assert.Equal(new UserPreferences(), store.Load());
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Load_PreservesPresentFieldWhenOtherFieldMissing()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "preferences.json");
        File.WriteAllText(path, "{\"Theme\":\"Dark\"}");
        Assert.Equal(new UserPreferences("Dark"), new JsonUserPreferencesStore(path).Load());
    }

    [Fact]
    public void Save_RecentOutputsRoundTrip()
    {
        var store = new JsonUserPreferencesStore(Path.Combine(_root, "preferences.json"));
        RecentOutput[] outputs =
        [
            new("result.pdf", "result.pdf", new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.FromHours(8))),
            new("output", "result.pdf 等 2 个", new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.FromHours(8)), true),
        ];
        store.Save(new("Dark", "output", outputs));
        var loaded = store.Load();
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("output", loaded.LastOutputDirectory);
        Assert.Equal(outputs, loaded.RecentOutputs);
    }

    [Fact]
    public void Load_OldJsonWithoutRecentOutputsPreservesPreferences()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "preferences.json");
        File.WriteAllText(path, """{"Theme":"Light","LastOutputDirectory":"output"}""");
        Assert.Equal(new UserPreferences("Light", "output"), new JsonUserPreferencesStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
