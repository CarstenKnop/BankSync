using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace BankSync.Api;

/// <summary>
/// Mints the RS256 JWT every Enable Banking call carries:
/// header typ=JWT, alg=RS256, kid=application id; claims iss=enablebanking.com, aud=api.enablebanking.com, iat, exp.
/// </summary>
internal sealed class JwtFactory : IDisposable
{
    public const string Issuer = "enablebanking.com";
    public const string Audience = "api.enablebanking.com";

    private readonly RSA _rsa;
    private readonly SigningCredentials _credentials;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private string? _cached;
    private DateTimeOffset _cachedExpires;

    public JwtFactory(string applicationId, string privateKeyPem, TimeSpan lifetime, TimeProvider time)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(24))
            throw new BankSyncConfigurationException("JWT lifetime must be between 0 and 24 hours.");
        _lifetime = lifetime;
        _time = time;
        _rsa = RSA.Create();
        try { _rsa.ImportFromPem(privateKeyPem); }
        catch (Exception ex) { throw new BankSyncConfigurationException("The private key could not be read. It must be an RSA key in PEM format (PKCS#1 or PKCS#8).", ex); }
        var key = new RsaSecurityKey(_rsa) { KeyId = applicationId };
        // The default provider factory caches signature providers by key id process-wide, which would keep a
        // reference to a disposed RSA if a second factory were created with the same application id.
        key.CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        _credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
    }

    public string GetToken()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_cached is not null && now < _cachedExpires - TimeSpan.FromMinutes(5))
                return _cached;

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: [new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
                notBefore: now.UtcDateTime,
                expires: (now + _lifetime).UtcDateTime,
                signingCredentials: _credentials);

            _cached = new JwtSecurityTokenHandler().WriteToken(token);
            _cachedExpires = now + _lifetime;
            return _cached;
        }
    }

    public void Dispose() => _rsa.Dispose();
}
