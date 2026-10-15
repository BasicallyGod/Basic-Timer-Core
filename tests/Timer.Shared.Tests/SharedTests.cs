using Timer.Core;
using Timer.Storage;
using Xunit;

namespace Timer.Shared.Tests;

public sealed class SharedTests
{
    // Core and Storage are shared with other apps, so they must never reach up to a UI library or an app.
    [Fact]
    public void TheLibrariesKnowNothingOfAnyUiOrApp()
    {
        foreach (var assembly in new[] { typeof(TrackingEngine).Assembly, typeof(TimerStore).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies().Select(a => a.Name!), n => n.StartsWith("Avalonia", StringComparison.Ordinal) || n.StartsWith("Timer.Desk", StringComparison.Ordinal) || n.StartsWith("BasicsAdvancedTimer", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(TrackingEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name), n => n == "Timer.Storage");
    }

    // An app keeps its own settings in the extras and the library hands them back untouched.
    [Fact]
    public void AnAppsOwnSettingsSurviveASaveAndReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "timer-shared-" + Guid.NewGuid());
        try
        {
            using (var store = new TimerStore(folder))
            {
                var engine = new TrackingEngine(); var settings = store.Load(engine);
                store.Save(engine, settings with { Extras = new SettingsExtras(new[] { "future-flag=7" }, new Dictionary<string, IReadOnlyList<string>> { ["someApp"] = new[] { "{\"a\":1}" } }) });
            }
            using (var store = new TimerStore(folder))
            {
                var settings = store.Load(new TrackingEngine());
                Assert.Contains("future-flag=7", settings.Extras!.AppearanceLines);
                Assert.Equal(new[] { "{\"a\":1}" }, settings.Extras.Sections["someApp"]);
            }
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
