using System.Security.Cryptography;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Channel binding: the TLS negotiated parameters when the transport exposes
/// them, the empty string when it does not (.NET's SslStream ships no RFC 5705
/// exporter, and the reference environment is plain HTTP — so <c>cbh</c> is
/// always present as a claim and <c>Link:RequireChannelBinding</c> is what
/// makes the empty value fatal).
/// </summary>
public static class LinkChannelBinding
{
    /// <summary>The claim name every proof carries.</summary>
    public const string Claim = "cbh";

    /// <summary><c>sha256:</c> + hex over the TLS parameters the socket
    /// negotiated, or the empty string when the connection is not TLS at all.</summary>
    public static string HashOf(IFeatureCollection features)
    {
        var tls = features.Get<ITlsHandshakeFeature>();
        if (tls is null)
        {
            return string.Empty;
        }

        using var sha = SHA256.Create();
        var material = $"{tls.Protocol}|{tls.NegotiatedCipherSuite}";
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
