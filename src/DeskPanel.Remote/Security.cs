using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DeskPanel.Remote;

/// <summary>PBKDF2 password hashing. Only the hash is stored (in the DPAPI-protected secret store), never the password.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int MinimumLength = 10;

    public static string? Validate(string password) =>
        password.Length < MinimumLength ? $"La contraseña debe tener al menos {MinimumLength} caracteres." : null;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            // Constant-time compare: a plain == leaks how many leading bytes matched.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Server-side sessions. The cookie only carries a random token; everything else stays in memory here.</summary>
internal sealed class SessionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<string, DateTime> _expiry = new();

    public string Create()
    {
        Purge();
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        _expiry[token] = DateTime.UtcNow + Lifetime;
        return token;
    }

    /// <summary>True for a live session; using it pushes its expiry forward, so an active phone stays signed in.</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_expiry.TryGetValue(token, out var expiry))
        {
            return false;
        }

        if (expiry < DateTime.UtcNow)
        {
            _expiry.TryRemove(token, out _);
            return false;
        }

        _expiry[token] = DateTime.UtcNow + Lifetime;
        return true;
    }

    public void Remove(string? token)
    {
        if (token is not null)
        {
            _expiry.TryRemove(token, out _);
        }
    }

    private void Purge()
    {
        var now = DateTime.UtcNow;
        foreach (var (token, expiry) in _expiry)
        {
            if (expiry < now)
            {
                _expiry.TryRemove(token, out _);
            }
        }
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Slows down password guessing: five failures from one address lock it out, for longer each time it happens again.
/// Per address, not global, so one noisy device cannot lock the owner out.
/// </summary>
internal sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private sealed class State
    {
        public int Failures;
        public DateTime WindowStart;
        public DateTime LockedUntil;
        public int Lockouts;
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly object _gate = new();

    public bool IsLocked(string address, out TimeSpan remaining)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(address, out var state) && state.LockedUntil > DateTime.UtcNow)
            {
                remaining = state.LockedUntil - DateTime.UtcNow;
                return true;
            }
        }

        remaining = TimeSpan.Zero;
        return false;
    }

    public void Failure(string address)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(address, out var state))
            {
                state = _states[address] = new State { WindowStart = DateTime.UtcNow };
            }

            if (DateTime.UtcNow - state.WindowStart > FailureWindow)
            {
                state.Failures = 0;
                state.WindowStart = DateTime.UtcNow;
            }

            if (++state.Failures >= MaxFailures)
            {
                // 5 min, 10, 20, 40, then 1 h at most.
                var minutes = Math.Min(60, 5 * Math.Pow(2, state.Lockouts));
                state.LockedUntil = DateTime.UtcNow.AddMinutes(minutes);
                state.Lockouts++;
                state.Failures = 0;
            }
        }
    }

    public void Success(string address)
    {
        lock (_gate)
        {
            _states.Remove(address);
        }
    }
}

/// <summary>Fixed one-minute window per address; a crude guard against a client hammering the server.</summary>
internal sealed class RateLimiter
{
    private readonly int _perMinute;
    private readonly Dictionary<string, (long Minute, int Count)> _counts = new();
    private readonly object _gate = new();
    private long _lastCleanup;

    public RateLimiter(int perMinute) => _perMinute = perMinute;

    public bool Allow(string address)
    {
        var minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        lock (_gate)
        {
            if (minute != _lastCleanup)
            {
                _lastCleanup = minute;
                foreach (var key in _counts.Where(kv => kv.Value.Minute < minute).Select(kv => kv.Key).ToList())
                {
                    _counts.Remove(key);
                }
            }

            var (m, count) = _counts.TryGetValue(address, out var current) && current.Minute == minute ? current : (minute, 0);
            _counts[address] = (m, count + 1);
            return count + 1 <= _perMinute;
        }
    }
}

/// <summary>
/// Opaque, signed identifiers for search hits. The page never sends a file path: it sends the id it was given, and
/// the server resolves it. A forged id fails the signature, so the endpoints cannot be pointed at arbitrary files.
/// The key is random per server start, so ids are only valid for the current run (search again after a restart).
/// </summary>
internal sealed class HitIds
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string Create(string indexName, string relativePath)
    {
        var payload = Encoding.UTF8.GetBytes(indexName + "\n" + relativePath);
        return SessionStore.Base64Url(payload) + "." + SessionStore.Base64Url(Sign(payload));
    }

    public bool TryParse(string? id, out string indexName, out string relativePath)
    {
        indexName = relativePath = "";
        var parts = id?.Split('.');
        if (parts is not { Length: 2 })
        {
            return false;
        }

        try
        {
            var payload = FromBase64Url(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Sign(payload), FromBase64Url(parts[1])))
            {
                return false;
            }

            var text = Encoding.UTF8.GetString(payload);
            var split = text.IndexOf('\n');
            if (split <= 0)
            {
                return false;
            }

            indexName = text[..split];
            relativePath = text[(split + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(_key, payload);

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}
