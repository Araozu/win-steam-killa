using Service;
using Shared;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "state-tests-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_dir, "state.json");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void CorruptPrimaryRecoversPreviousDurableState()
    {
        var store = new StateStore(FilePath);
        store.Save(new() { LogicalDay = "2026-11-14", UsedSeconds = 120 });
        store.Save(new() { LogicalDay = "2026-11-14", UsedSeconds = 135 });
        File.WriteAllText(FilePath, "{ interrupted write");
        Assert.Equal(120, store.Load()!.UsedSeconds);
    }

    [Fact]
    public void CorruptStateWithoutBackupCannotSilentlyEraseUsage()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "null");
        Assert.Throws<InvalidDataException>(() => new StateStore(FilePath).Load());
    }

    [Fact]
    public void CopyDoesNotShareMutableGameHistory()
    {
        var original = new TimerState
        {
            SeenGames = ["game"], GameHistory = [new() { Name = "game", TotalSeconds = 120 }],
        };
        var copy = original.Copy();
        copy.SeenGames.Clear();
        copy.GameHistory[0].TotalSeconds = 0;
        Assert.Single(original.SeenGames);
        Assert.Equal(120, original.GameHistory[0].TotalSeconds);
    }
}
