using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace EnableBanking.Sample;

/// <summary>
/// Mints the short-lived RS256 JWT that every Enable Banking API call carries.
/// Header: typ=JWT, alg=RS256, kid=&lt;application id&gt;.
/// Claims: iss=enablebanking.com, aud=api.enablebanking.com, iat, exp (max 24 h).
/// The RSA key is loaded once; tokens are cached and renewed 5 minutes before expiry.
/// </summary>
public sealed class JwtFactory : IDisposable
{
    public const string Issuer = "enablebanking.com";
    public const string Audience = "api.enablebanking.com";

    private readonly RSA _rsa;
    private readonly SigningCredentials _credentials;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();
    private string? _cached;
    private DateTimeOffset _cachedExpires;

    public JwtFactory(string applicationId, string privateKeyPem, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(applicationId)) throw new ArgumentException("Application id is required", nameof(applicationId));
        _lifetime = lifetime ?? TimeSpan.FromMinutes(30);
        if (_lifetime > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lifetime), "Enable Banking rejects tokens valid for more than 24 hours.");

        _rsa = RSA.Create();
        _rsa.ImportFromPem(privateKeyPem);                 // PKCS#1 or PKCS#8 PEM
        var key = new RsaSecurityKey(_rsa) { KeyId = applicationId };   // KeyId becomes the "kid" header
        _credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
    }

    public static JwtFactory FromFile(string applicationId, string privateKeyPath)
        => new(applicationId, File.ReadAllText(privateKeyPath));

    public string GetToken()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cached is not null && now < _cachedExpires - TimeSpan.FromMinutes(5))
                return _cached;

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: new[]
                {
                    new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
                },
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
