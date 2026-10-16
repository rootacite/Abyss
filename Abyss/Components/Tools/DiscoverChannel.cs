using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace Abyss.Components.Tools;

public sealed record DiscoverEndpoint(string Host, int Port, string Magic);

public static class DiscoverChannel
{
    public const string TestKeyHex = "d6be2813950d3d48a394b5b195781ed893b9a1d4d40194185481583cb49a4169";
    public const string TestPacketHex =
        "000102030405060708090a0bd1da5144e8feccfe6019d201cb5319dda87f77922a8ab24af26d83ef038fffc4901d1f246cba0792cfdfb850323e29aa7e9d69e280f641c2528f6ea55e3c1895755f2b36204571408e5dbd20";
    public const string TestPlain = "{\"v\":1,\"op\":\"hello\",\"id\":\"00112233445566778899aabbccddeeff\"}";

    private static readonly byte[] Salt = Convert.FromHexString("A6595C103E7B2184C04D916A2FE8770B");
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("Abyss-Discover-V1");

    public static void AssertTestVector()
    {
        var key = DeriveKey("kitchen");
        if (!Hex(key).Equals(TestKeyHex, StringComparison.Ordinal))
            throw new InvalidOperationException("discover hkdf mismatch");

        var nonce = new byte[12];
        for (var i = 0; i < nonce.Length; i++)
            nonce[i] = (byte)i;

        var plain = Encoding.UTF8.GetBytes(TestPlain);
        var packet = Seal(plain, "kitchen", nonce);
        if (!Hex(packet).Equals(TestPacketHex, StringComparison.Ordinal))
            throw new InvalidOperationException("discover aead mismatch");

        var opened = Open(packet, "kitchen");
        if (opened == null || !opened.AsSpan().SequenceEqual(plain))
            throw new InvalidOperationException("discover open mismatch");
        if (Open(packet, "other") != null)
            throw new InvalidOperationException("discover accepted a different magic");
    }

    public static bool TryParse(string raw, out DiscoverEndpoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        const string prefix = "abyssc://";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        text = text[prefix.Length..].TrimEnd('/');
        if (text.Length == 0 || text.Contains('/'))
            return false;

        if (text[0] == '[')
        {
            var end = text.IndexOf(']');
            if (end <= 1)
                return false;
            var host = text[1..end];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return false;
            var rest = text[(end + 1)..];
            if (rest.Length == 0 || rest[0] != ':')
                return false;
            return SplitPortAndMagic(host, rest[1..], out endpoint);
        }

        var parts = text.Split(':');
        if (parts.Length == 2)
            return Finish(parts[0], 1883, parts[1], out endpoint);
        if (parts.Length == 3 && IsPort(parts[1]))
            return Finish(parts[0], int.Parse(parts[1]), parts[2], out endpoint);
        return false;
    }

    public static string Topic(string magic)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(magic));
        return "abyss/c/" + Hex(hash);
    }

    public static string HelloJson(string id) =>
        "{\"v\":1,\"op\":\"hello\",\"id\":\"" + id + "\"}";

    public static string OfferJson(string id, string url) =>
        "{\"v\":1,\"op\":\"offer\",\"id\":\"" + id + "\",\"url\":" + Quote(url) + "}";

    public static byte[] Seal(byte[] plaintext, string magic, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(12);
        if (nonce.Length != 12)
            throw new ArgumentException("Nonce must be 12 bytes", nameof(nonce));

        var keyBytes = DeriveKey(magic);
        using var key = Key.Import(AeadAlgorithm.ChaCha20Poly1305, keyBytes, KeyBlobFormat.RawSymmetricKey);
        CryptographicOperations.ZeroMemory(keyBytes);
        var cipher = AeadAlgorithm.ChaCha20Poly1305.Encrypt(key, nonce, ReadOnlySpan<byte>.Empty, plaintext);
        var packet = new byte[nonce.Length + cipher.Length];
        nonce.CopyTo(packet, 0);
        cipher.CopyTo(packet, nonce.Length);
        return packet;
    }

    public static byte[]? Open(byte[] packet, string magic)
    {
        if (packet.Length < 12 + 16)
            return null;
        try
        {
            var keyBytes = DeriveKey(magic);
            using var key = Key.Import(AeadAlgorithm.ChaCha20Poly1305, keyBytes, KeyBlobFormat.RawSymmetricKey);
            CryptographicOperations.ZeroMemory(keyBytes);
            var nonce = packet.AsSpan(0, 12);
            var body = packet.AsSpan(12);
            var plain = new byte[body.Length - 16];
            if (!AeadAlgorithm.ChaCha20Poly1305.Decrypt(key, nonce, ReadOnlySpan<byte>.Empty, body, plain))
                return null;
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static byte[] DeriveKey(string magic)
    {
        return KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(
            Encoding.UTF8.GetBytes(magic),
            Salt,
            Info,
            32);
    }

    private static bool SplitPortAndMagic(string host, string rest, out DiscoverEndpoint endpoint)
    {
        var idx = rest.IndexOf(':');
        if (idx < 0)
            return Finish(host, 1883, rest, out endpoint);
        if (!IsPort(rest[..idx]))
        {
            endpoint = null!;
            return false;
        }
        return Finish(host, int.Parse(rest[..idx]), rest[(idx + 1)..], out endpoint);
    }

    private static bool Finish(string host, int port, string magic, out DiscoverEndpoint endpoint)
    {
        if (host.Length == 0 || magic.Length == 0 || magic.Contains(':') || magic.Contains('/'))
        {
            endpoint = null!;
            return false;
        }
        endpoint = new DiscoverEndpoint(host, port, magic);
        return true;
    }

    private static bool IsPort(string text)
    {
        if (text.Length is 0 or > 5 || !text.All(char.IsDigit))
            return false;
        var port = int.Parse(text);
        return port is >= 1 and <= 65535;
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
