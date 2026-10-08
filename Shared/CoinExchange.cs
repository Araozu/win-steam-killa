using System.Text.Json.Serialization;

namespace Shared;

public static class CoinExchangeRates
{
    public const string Command = "coin-exchange";
    public const int ProtocolVersion = 2;
    public const decimal SoldCoins = 1;
    public const int SaleSeconds = 60;
    public const decimal BoughtCoins = 1;
    public const int PurchaseSeconds = 66;
    public const int MaximumSaleCoins = int.MaxValue / SaleSeconds;
}

// One outstanding exchange per wallet. The service retains its latest receipt,
// including rejected trades, so retrying after a lost reply never applies twice.
public sealed record CoinExchangeRequest(Guid WalletId, long Number, string LogicalDay, bool BuyCoins, int Coins = 0)
{
    // Requests saved before quantities existed bought 10 or sold 1. Keep their
    // exact values when recovering a pending trade or replaying an old receipt.
    [JsonIgnore]
    public int CoinCount => Coins == 0 ? BuyCoins ? 10 : 1 : Coins;
    [JsonIgnore]
    public bool HasValidAmount => Coins >= 0 && (BuyCoins ? Coins is 0 or 1 : CoinCount is >= 1 and <= CoinExchangeRates.MaximumSaleCoins);
    [JsonIgnore]
    public int TimeSeconds => checked(CoinCount * (BuyCoins ? CoinExchangeRates.PurchaseSeconds : CoinExchangeRates.SaleSeconds));
}
public sealed record CoinExchangeReceipt(CoinExchangeRequest Request, bool Applied, string Error = "");
