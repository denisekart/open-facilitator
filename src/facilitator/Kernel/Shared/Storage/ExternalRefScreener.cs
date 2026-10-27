using System.Net;
using System.Net.Sockets;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// The external-ref screen (spec §15.4: external refs are <c>unverified</c>
/// and the kernel NEVER fetches them — a declaration is a claim, and only a
/// claim an operator bound AND that survives the SSRF screen upgrades to
/// <c>verified</c>). Laws, in order: absolute http(s) URL with no userinfo;
/// a binding (operator-declared prefix) waives the address screen but never
/// the syntax laws; unbound URLs resolve and EVERY address must sit outside
/// loopback / private / link-local / unique-local space (a workspace
/// declaring <c>http://169.254.169.254/latest/meta-data</c> is a claim about
/// someone else's network, not a trust claim). The screen NEVER refuses a
/// registration — a failure downgrades to <c>unverified</c> + an audit
/// reason; refusal stays reserved for declaration contradictions.
/// </summary>
public static class ExternalRefScreener
{
    /// <summary>Screen one declared <c>storageUrl</c>. Returns the outcome + a machine reason for the audit trail.</summary>
    public static async Task<(bool Allowed, string Reason)> ScreenAsync(
        string storageUrl, IReadOnlyList<ExternalBinding> bindings)
    {
        if (!Uri.TryCreate(storageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (false, "not-absolute-http");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return (false, "userinfo");
        }

        var binding = bindings.FirstOrDefault(b =>
            storageUrl.StartsWith(b.UrlPrefix, StringComparison.Ordinal));
        if (binding is not null)
        {
            // Operator trust: the binding IS the address claim.
            return (true, $"bound:{binding.Id}");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);
        }
        catch (SocketException)
        {
            return (false, "dns-failure");
        }

        if (addresses.Length == 0)
        {
            return (false, "no-address");
        }

        foreach (var address in addresses)
        {
            if (IsNonPublic(address))
            {
                return (false, "private-address");
            }
        }

        return (true, "screened");
    }

    private static bool IsNonPublic(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal
        || address.IsIPv6SiteLocal
        || address.IsIPv6Teredo
        || IsPrivateV4(address)
        || IsUniqueLocalV6(address);

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || b[0] == 172 && b[1] is >= 16 and <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254
            || b[0] == 127
            || b[0] == 0;
    }

    private static bool IsUniqueLocalV6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 0xFC || b[0] == 0xFD;
    }
}
