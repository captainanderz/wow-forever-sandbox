// Local opt-in TLS configuration. Does not export keys or change certificate stores.
using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace AuthServer;

internal static class LocalTls
{
    private static X509Certificate2 selected;
    // The unmodified beta expands a bare "localhost" portal token to this canonical name and
    // validates the server certificate against it. Listeners still bind loopback only; a
    // local hosts entry maps the name to 127.0.0.1.
    public const string CanonicalLoopbackName = "localhost.actual.battle.net";
    public static string Hostname { get; private set; } = "127.0.0.1";
    public static string CertificateThumbprint => selected?.Thumbprint;
    public static bool? ChainTrusted { get; private set; }
    public static string[] ChainStatus { get; private set; } = Array.Empty<string>();

    public static void Configure(string thumbprint, string hostname)
    {
        Hostname = hostname ?? (string.IsNullOrWhiteSpace(thumbprint) ? "127.0.0.1" : "localhost");
        if (Hostname != "localhost" && Hostname != "127.0.0.1" && Hostname != CanonicalLoopbackName)
            throw new ArgumentException("--hostname must be localhost, 127.0.0.1 or " + CanonicalLoopbackName + ".");
        if (string.IsNullOrWhiteSpace(thumbprint)) return;
        if (Hostname == "127.0.0.1")
            throw new ArgumentException("A selected developer certificate requires a DNS --hostname (localhost or " + CanonicalLoopbackName + ").");
        var normalized = thumbprint.Replace(" ", "").ToUpperInvariant();
        if (normalized.Length != 40 || !normalized.All(Uri.IsHexDigit))
            throw new ArgumentException("--certificate-thumbprint requires a 40-character certificate thumbprint.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        selected = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, false).Cast<X509Certificate2>().SingleOrDefault();
        if (selected == null || !selected.HasPrivateKey)
            throw new ArgumentException("Selected certificate with private key was not found in CurrentUser/My.");
        var now = DateTime.UtcNow;
        if (now < selected.NotBefore.ToUniversalTime() || now > selected.NotAfter.ToUniversalTime())
            throw new ArgumentException("Selected certificate is outside its validity interval.");
        if (!selected.MatchesHostname(Hostname, allowWildcards: false, allowCommonName: false))
            throw new ArgumentException($"Selected certificate requires an exact {Hostname} subject alternative name.");
        if (!selected.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(extension => extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1")))
            throw new ArgumentException("Selected certificate does not declare server authentication usage.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        ChainTrusted = chain.Build(selected);
        ChainStatus = chain.ChainStatus.Select(item => item.Status.ToString()).ToArray();
    }

    public static X509Certificate ServerCertificate() => selected ?? (X509Certificate)new X509Certificate(Globals.CertData);
}
