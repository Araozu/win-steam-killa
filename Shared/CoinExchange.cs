namespace Shared;

public static class CoinExchangeRates
{
    public const string Command = "coin-exchange";
    public const decimal SoldCoins = 1;
    public const int SaleSeconds = 60;
    public const decimal BoughtCoins = 10;
    public const int PurchaseSeconds = 11 * 60;
}

// One outstanding exchange per wallet. The service retains its latest receipt,
// including rejected trades, so retrying after a lost reply never applies twice.
public sealed record CoinExchangeRequest(Guid WalletId, long Number, string LogicalDay, bool BuyCoins);
public sealed record CoinExchangeReceipt(CoinExchangeRequest Request, bool Applied, string Error = "");
