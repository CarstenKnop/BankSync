using System.Security.Cryptography;

namespace BankSync.Core;

/// <summary>
/// AES-GCM encryption for secrets at rest (the Enable Banking session id). The key lives in a file next to the
/// database; it is generated on first use. Losing the key only means the owner has to consent again.
/// </summary>
internal sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            _key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            if (_key.Length != 32) throw new BankSyncConfigurationException($"Key file '{keyPath}' does not contain a 256-bit key.");
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        var dir = Path.GetDirectoryName(Path.GetFullPath(keyPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(keyPath, Convert.ToBase64String(_key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedText)
    {
        var blob = Convert.FromBase64String(protectedText);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = blob.AsSpan(0, nonceLen);
        var tag = blob.AsSpan(nonceLen, tagLen);
        var cipher = blob.AsSpan(nonceLen + tagLen);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
