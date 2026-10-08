using App.Game;
using Shared;

namespace Tests;

internal static class WalletFixture
{
    // Keep existing round/trade scenarios funded independently of the allowance.
    // This also exercises compatibility with an existing daily-grant wallet.
    public static FlightWallet Open(string path, DateTime localNow)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new FlightWalletState { Balance = 100, LastGrantDay = LogicalDay.ForLocal(localNow) };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state));
        }
        return new FlightWallet(path, localNow);
    }
}
