using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Rest;

/// <summary>
/// Issues HS256-signed JWT access tokens and remembers them, so they can be revoked.
/// Tokens live in memory: they survive server restarts but not closing the app.
/// </summary>
internal sealed class TokenService
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, IssuedToken> _tokens = new(StringComparer.Ordinal);

    public IssuedToken Issue(string clientId, string? scope, TimeSpan lifetime, string issuer)
    {
        PurgeExpired();
        var now = DateTime.UtcNow;
        // Whole seconds so iat/exp in the token match the record exactly.
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var expires = now + lifetime;

        var header = new JsonObject { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new JsonObject
        {
            ["iss"] = issuer,
            ["sub"] = clientId,
            ["client_id"] = clientId,
            ["iat"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["nbf"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(expires).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (!string.IsNullOrEmpty(scope)) payload["scope"] = scope;

        var signingInput = Encode(header) + "." + Encode(payload);
        var signature = ClientCredential.Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(signingInput)));
        var token = new IssuedToken(signingInput + "." + signature, clientId, string.IsNullOrEmpty(scope) ? null : scope, now, expires);
        _tokens[token.AccessToken] = token;
        return token;
    }

    /// <summary>The token's record when it was issued here, hasn't expired and hasn't been revoked.</summary>
    public IssuedToken? Validate(string accessToken)
    {
        if (!_tokens.TryGetValue(accessToken, out var token)) return null;
        if (token.ExpiresUtc > DateTime.UtcNow) return token;
        _tokens.TryRemove(accessToken, out _);
        return null;
    }

    public int ActiveCount
    {
        get
        {
            PurgeExpired();
            return _tokens.Count;
        }
    }

    public void RevokeAll() => _tokens.Clear();

    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, token) in _tokens)
            if (token.ExpiresUtc <= now) _tokens.TryRemove(key, out _);
    }

    private static string Encode(JsonObject o) => ClientCredential.Base64Url(Encoding.UTF8.GetBytes(o.ToJsonString()));
}
