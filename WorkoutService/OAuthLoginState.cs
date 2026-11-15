using System.Security.Cryptography;

// Only authenticated owners can initiate a link; callbacks must come from the
// same browser and consume a short-lived, one-use server nonce.
sealed class OAuthLoginState
{
    public const string CookieName = "steamtimer-oauth";
    private readonly Dictionary<string, DateTimeOffset> _pending = new();
    private readonly object _gate = new();

    public string Create()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _pending.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                _pending.Remove(key);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _pending[state] = now.AddMinutes(10);
            return state;
        }
    }

    public bool Consume(string? state, string? cookie)
    {
        if (state is null || state.Length != 64 || cookie is null || cookie.Length != 64
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(cookie)))
            return false;
        lock (_gate)
            return _pending.Remove(state, out var expiry) && expiry > DateTimeOffset.UtcNow;
    }
}
