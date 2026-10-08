using SideBySide.Core.Compare;
using SideBySide.Core.Filtering;
using SideBySide.Core.Sessions;
using SideBySide.Core.Settings;

namespace SideBySide.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var d = new TempDir();
        var s = new JsonSettingsStore(Path.Combine(d.Path, "none.json")).Load();
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.NotEmpty(s.FilterPresets);
    }

    [Fact]
    public void Roundtrip()
    {
        using var d = new TempDir();
        var store = new JsonSettingsStore(Path.Combine(d.Path, "sub", "settings.json"));
        var s = new AppSettings
        {
            Theme = ThemeMode.Dark,
            Filter = new FilterSettings { Exclude = "*.tmp" },
            Compare = new CompareOptions { Method = CompareMethod.Content, TimeToleranceSeconds = 5 },
        };
        s.Left.Tabs.Add(@"C:\work");
        s.Sessions.Add(new CompareSession { Name = "proj", LeftPath = "a", RightPath = "b" });
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal("*.tmp", loaded.Filter.Exclude);
        Assert.Equal(CompareMethod.Content, loaded.Compare.Method);
        Assert.Equal(5, loaded.Compare.TimeToleranceSeconds);
        Assert.Equal(@"C:\work", Assert.Single(loaded.Left.Tabs));
        Assert.Equal("proj", Assert.Single(loaded.Sessions).Name);
        Assert.True(double.IsNaN(loaded.Window.Left));
    }

    [Fact]
    public void Corrupted_file_is_backed_up()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, "{ not json");
        var s = new JsonSettingsStore(path).Load();
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Legacy_file_without_version_is_migrated()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, """{ "Theme": "Light", "DualPane": false }""");
        var s = new JsonSettingsStore(path).Load();
        Assert.Equal(ThemeMode.Light, s.Theme);
        Assert.False(s.DualPane);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
    }
}
