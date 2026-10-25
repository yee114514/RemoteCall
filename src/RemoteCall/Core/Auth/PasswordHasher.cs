using System.Security.Cryptography;
using System.Text;

namespace RemoteCall.Core.Auth;

/// <summary>
/// PBKDF2-SHA256 密码哈希。只保存盐与派生结果，不保存明文。
/// </summary>
public static class PasswordHasher
{
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int MinPasswordLength = 4;
    public const int MaxPasswordLength = 64;

    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static bool IsAcceptablePassword(string? password)
    {
        if (password is null)
        {
            return false;
        }

        var length = password.Length;
        return length >= MinPasswordLength && length <= MaxPasswordLength;
    }

    public static (byte[] Salt, byte[] Hash) Create(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return (salt, hash);
    }

    public static bool Verify(string password, string saltBase64, string hashBase64, int iterations)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            var expected = Convert.FromBase64String(hashBase64);
            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成一个便于手输的随机密码（去掉易混淆字符）。
    /// </summary>
    public static string GeneratePassword(int length = 8)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var builder = new StringBuilder(length);
        foreach (var b in bytes)
        {
            builder.Append(Alphabet[b % Alphabet.Length]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成会话令牌（Base64Url）。
    /// </summary>
    public static string CreateSessionToken()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// 会话令牌只以 SHA256 的形式保存。
    /// </summary>
    public static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
