using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Abyss.Components.Tools;

public static class PublicAddress
{
    public static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in props.UnicastAddresses)
                yield return unicast.Address;
        }
    }

    /// <summary>
    /// 0 global IPv6, 1 public IPv4, 2 ULA, 3 private IPv4. Null means the address is not offered.
    /// </summary>
    public static int? Rank(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv4MappedToIPv6)
            return null;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return null;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
                return null;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
                return 2;
            if ((b[0] & 0xE0) == 0x20)
                return 0;
            return null;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] >= 224)
                return null;
            if (b[0] == 169 && b[1] == 254)
                return null;
            var rfc1918 = b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168);
            var cgnat = b[0] == 100 && b[1] is >= 64 and <= 127;
            if (rfc1918 || cgnat)
                return 3;
            return 1;
        }

        return null;
    }

    public static string? Choose(IEnumerable<IPAddress> addresses, IEnumerable<string> listenUrls)
    {
        var listeners = listenUrls.Select(ParseListen).OfType<Listen>().ToList();
        if (listeners.Count == 0)
            return null;

        var ranked = addresses
            .Select(ip => (ip, rank: Rank(ip)))
            .Where(item => item.rank.HasValue)
            .OrderBy(item => item.rank)
            .ThenBy(item => item.ip.GetAddressBytes(), AddressBytes.Instance)
            .ToList();

        return Match(ranked, listeners);
    }

    /// <summary>
    /// Address offered over abyssc. The host is a global IPv6 address. The port is the
    /// loopback HTTP port the tunnel forwards to, not the public tunnel port 4096.
    /// </summary>
    public static string? AbyssUrl(IEnumerable<IPAddress> addresses, IEnumerable<string> listenUrls)
    {
        var global = addresses
            .Select(ip => (ip, rank: Rank(ip)))
            .Where(item => item.rank == 0)
            .OrderBy(item => item.ip.GetAddressBytes(), AddressBytes.Instance)
            .Select(item => item.ip)
            .FirstOrDefault();
        if (global == null)
            return null;

        var port = HttpLoopbackPort(listenUrls) ?? 80;
        return $"abyss://[{global}]:{port}/";
    }

    private static int? HttpLoopbackPort(IEnumerable<string> listenUrls)
    {
        int? anyHttp = null;
        foreach (var raw in listenUrls)
        {
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
                continue;
            if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                continue;
            if (uri.IdnHost is "localhost" or "::1" or "127.0.0.1")
                return uri.Port;
            anyHttp ??= uri.Port;
        }

        return anyHttp;
    }

    private static string? Match(List<(IPAddress ip, int? rank)> ranked, List<Listen> listeners)
    {
        foreach (var (ip, _) in ranked)
        {
            var matches = listeners.Where(listen => Matches(listen, ip)).ToList();
            if (matches.Count == 0)
                continue;
            var best = matches
                .OrderBy(listen => listen.Scheme == "https" ? 0 : 1)
                .ThenBy(listen => listen.Port)
                .First();
            var host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
            return $"{best.Scheme}://{host}:{best.Port}/";
        }

        return null;
    }

    public static string EnsureTrailingSlash(string url)
    {
        var trimmed = url.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    private static bool Matches(Listen listen, IPAddress ip)
    {
        if (listen.Specific != null)
            return listen.Specific.Equals(ip);
        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        return v6 ? listen.WildcardV6 : listen.WildcardV4;
    }

    private static Listen? ParseListen(string raw)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return null;
        // IdnHost drops the brackets that Uri.Host keeps around IPv6 literals.
        var host = uri.IdnHost;
        var wildcardV4 = false;
        var wildcardV6 = false;
        IPAddress? specific = null;
        if (host == "0.0.0.0")
            wildcardV4 = true;
        else if (host == "::")
            wildcardV6 = true;
        else if (host is "+" or "*" or "")
        {
            wildcardV4 = true;
            wildcardV6 = true;
        }
        else if (IPAddress.TryParse(host, out var parsed))
            specific = parsed;
        else
            return null;

        return new Listen(uri.Scheme, wildcardV4, wildcardV6, specific, uri.Port);
    }

    private sealed record Listen(string Scheme, bool WildcardV4, bool WildcardV6, IPAddress? Specific, int Port);

    private sealed class AddressBytes : IComparer<byte[]>
    {
        public static readonly AddressBytes Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null)
                return 0;
            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                var cmp = x[i].CompareTo(y[i]);
                if (cmp != 0)
                    return cmp;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
