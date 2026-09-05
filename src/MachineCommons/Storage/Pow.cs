using System.Security.Cryptography;
using System.Text;

namespace MachineCommons.Storage;

public static class Pow
{
    public static string Solve(string prefix, int difficulty, CancellationToken ct = default)
    {
        if (difficulty <= 0) return "0";
        var targetPrefix = new string('0', difficulty);
        long n = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = n.ToString();
            if (HashHex(prefix + candidate).StartsWith(targetPrefix, StringComparison.Ordinal))
                return candidate;
            n++;
        }
    }

    public static bool Verify(string prefix, string nonce, int difficulty)
    {
        if (difficulty <= 0) return true;
        if (string.IsNullOrEmpty(nonce)) return false;
        var targetPrefix = new string('0', difficulty);
        return HashHex(prefix + nonce).StartsWith(targetPrefix, StringComparison.Ordinal);
    }

    public static string HashHex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
