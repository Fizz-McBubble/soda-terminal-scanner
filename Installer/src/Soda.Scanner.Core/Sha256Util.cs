using System.Security.Cryptography;

namespace Soda.Scanner.Core;

public static class Sha256Util
{
    public static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ComputeStreamSha256(stream);
    }

    public static string ComputeStreamSha256(Stream stream)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
