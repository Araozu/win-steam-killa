using App;

namespace Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void FlightClubVisibilityRoundTripsAndDefaultsToVisible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(directory, "app-settings.json");
            var settings = new AppSettings(path);
            Assert.True(settings.FlightClubVisible);

            settings.SetFlightClubVisible(false);
            Assert.False(settings.FlightClubVisible);
            Assert.True(File.Exists(path));

            var reopened = new AppSettings(path);
            Assert.False(reopened.FlightClubVisible);

            reopened.SetFlightClubVisible(true);
            Assert.True(new AppSettings(path).FlightClubVisible);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CorruptSettingsFallBackToVisible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SteamTimerTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "app-settings.json");
            File.WriteAllText(path, "not json");
            Assert.True(new AppSettings(path).FlightClubVisible);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
