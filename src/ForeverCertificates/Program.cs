// Creates new loopback-only test material. No certificate-store access.
//   <new directory> [--days N]          root + leaf valid N days (default 365); the root key is never stored
//   --verify-trust <directory>          read-only Windows chain validation
//   --persist-approved-root <directory> re-add the approved public root (identity from that directory's metadata.json)
using System.Formats.Asn1;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;

if (args.Length == 2 && args[0] == "--persist-approved-root")
{
    var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(args[1]), "metadata.json"))).RootElement;
    var encoded = File.ReadAllBytes(Path.Combine(Path.GetFullPath(args[1]), "root.cer"));
    if (Convert.ToHexString(SHA256.HashData(encoded)) != approved.GetProperty("rootPublicSha256").GetString())
        throw new CryptographicException("Not the exact approved public root.");
    // CurrentUser only, existing physical system-registry Root store. No
    // unprotected flag, policy modification, machine store or private key.
    // ProtectedRoots already records the user's explicit acceptance of this hash.
    using (var logical = new X509Store(StoreName.Root, StoreLocation.CurrentUser)) {
        logical.Open(OpenFlags.ReadOnly);
        if (logical.Certificates.Find(X509FindType.FindByThumbprint, approved.GetProperty("rootThumbprint").GetString(), false).Count != 0)
            throw new InvalidOperationException("Approved root is already present; no replacement needed.");
    }
    var storeHandle = NativeStore.CertOpenStore(new IntPtr(13), 0, IntPtr.Zero, 0x00010000 | 0x00004000, "Root");
    if (storeHandle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    try {
        if (!NativeStore.CertAddEncodedCertificateToStore(storeHandle, 1, encoded, (uint)encoded.Length, 1, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!NativeStore.CertControlStore(storeHandle, 0, 3, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    } finally { NativeStore.CertCloseStore(storeHandle, 0); }
    Console.WriteLine("{\"action\":\"persist-approved-public-root\",\"store\":\"CurrentUser/Root\",\"machineWideChanges\":false}");
    return;
}

if (args.Length == 2 && args[0] == "--verify-trust")
{
    // Fresh process, read-only stores. This avoids reusing the importer's cached
    // Windows chain engine immediately after a protected-root confirmation.
    var prepared = Path.GetFullPath(args[1]);
    using var publicRoot = new X509Certificate2(Path.Combine(prepared, "root.cer"));
    using var publicLeaf = new X509Certificate2(Path.Combine(prepared, "leaf.cer"));
    using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
    rootStore.Open(OpenFlags.ReadOnly);
    var rootPresent = rootStore.Certificates.Find(X509FindType.FindByThumbprint, publicRoot.Thumbprint, false).Count == 1;
    using var chain = new X509Chain();
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    chain.ChainPolicy.ExtraStore.Add(publicRoot); // Discovery only; grants no trust.
    var chainValid = chain.Build(publicLeaf);
    var exactRoot = chain.ChainElements.Count == 2 && chain.ChainElements[1].Certificate.Thumbprint == publicRoot.Thumbprint;
    var result = new {
        passed = rootPresent && chainValid && exactRoot,
        normalWindowsValidation = true, rootPresent, chainValid, exactRoot,
        rootThumbprint = publicRoot.Thumbprint, leafThumbprint = publicLeaf.Thumbprint,
        statuses = chain.ChainStatus.Select(item => item.Status.ToString()).ToArray(),
        storeChanges = false
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    Environment.ExitCode = result.passed ? 0 : 1;
    return;
}

var validityDays = 365;
if (args.Length == 3 && args[1] == "--days") validityDays = int.Parse(args[2]);
else if (args.Length != 1) throw new ArgumentException("Supply a NEW workspace output directory (optionally --days N).");
if (validityDays is < 1 or > 825) throw new ArgumentException("Validity must be 1-825 days.");
var destination = Path.GetFullPath(args[0]);
if (Directory.Exists(destination)) throw new IOException("Refusing to replace existing test material.");
var directory = Directory.CreateDirectory(destination);
var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("User SID missing.");
var permissions = new DirectorySecurity();
permissions.SetAccessRuleProtection(true, false);
permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
directory.SetAccessControl(permissions);

var now = DateTimeOffset.UtcNow;
var expiry = now.AddDays(validityDays);
using var rootKey = RSA.Create(3072); // Ephemeral: never exported or persisted.
var rootRequest = new CertificateRequest("CN=Forever Sandbox Loopback Test Root", rootKey,
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
rootRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
var constraints = new AsnWriter(AsnEncodingRules.DER);
constraints.PushSequence();
var permittedTag = new Asn1Tag(TagClass.ContextSpecific, 0, true);
constraints.PushSequence(permittedTag);
foreach (var name in new[] { "localhost", "127.0.0.1" })
{
    constraints.PushSequence();
    constraints.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
    constraints.PopSequence();
}
constraints.PushSequence();
constraints.WriteOctetString(new byte[] {127,0,0,1,255,255,255,255}, new Asn1Tag(TagClass.ContextSpecific, 7));
constraints.PopSequence();
constraints.PopSequence(permittedTag);
constraints.PopSequence();
var constraintBytes = constraints.Encode();
rootRequest.CertificateExtensions.Add(new X509Extension("2.5.29.30", constraintBytes, true));
using var root = rootRequest.CreateSelfSigned(now.AddMinutes(-5), expiry);

CertificateRequest NewLeaf(RSA key, string dnsName, bool loopback)
{
    var request = new CertificateRequest("CN=" + dnsName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName(dnsName);
    if (loopback)
    {
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
    }
    request.CertificateExtensions.Add(names.Build());
    var authority = new AsnWriter(AsnEncodingRules.DER);
    authority.PushSequence();
    authority.WriteOctetString(Convert.FromHexString(root.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!),
        new Asn1Tag(TagClass.ContextSpecific, 0));
    authority.PopSequence();
    request.CertificateExtensions.Add(new X509Extension("2.5.29.35", authority.Encode(), false));
    return request;
}

(bool ok, string[] statuses) Verify(X509Certificate2 certificate)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.DisableCertificateDownloads = true;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
    var ok = chain.Build(certificate);
    return (ok, chain.ChainStatus.Select(item => item.Status.ToString()).ToArray());
}

using var leafKey = RSA.Create(3072);
using var issued = NewLeaf(leafKey, "127.0.0.1", true).Create(root, now.AddMinutes(-4), expiry.AddMinutes(-1), RandomNumberGenerator.GetBytes(16));
using var leaf = issued.CopyWithPrivateKey(leafKey);
var verified = Verify(leaf);
if (!verified.ok) throw new CryptographicException("Prepared loopback chain failed custom-root validation: " + string.Join(",",verified.statuses));
using var forbiddenKey = RSA.Create(2048);
using var forbidden = NewLeaf(forbiddenKey, "example.com", false).Create(root, now.AddMinutes(-4), expiry.AddMinutes(-1), RandomNumberGenerator.GetBytes(16));
var negative = Verify(forbidden);
if (negative.ok || !negative.statuses.Any(x => x.Contains("NotPermittedNameConstraint")))
    throw new CryptographicException("Name constraints did not reject the non-loopback DNS fixture.");
if (!leaf.MatchesHostname("localhost", false, false) || !leaf.MatchesHostname("127.0.0.1", false, false))
    throw new CryptographicException("Loopback SAN checks failed.");

var rootPublic = root.Export(X509ContentType.Cert);
var leafPublic = leaf.Export(X509ContentType.Cert);
var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
File.WriteAllBytes(Path.Combine(destination,"root.cer"),rootPublic);
File.WriteAllBytes(Path.Combine(destination,"leaf.cer"),leafPublic);
File.WriteAllBytes(Path.Combine(destination,"leaf.pfx"),leaf.Export(X509ContentType.Pfx,password));
File.WriteAllText(Path.Combine(destination,"password.txt"),password);
var metadata = new {
    rootThumbprint = root.Thumbprint, leafThumbprint = leaf.Thumbprint,
    rootSubject = root.Subject, leafSubject = leaf.Subject, leafIssuer = leaf.Issuer,
    rootPublicSha256 = Convert.ToHexString(SHA256.HashData(rootPublic)),
    leafPublicSha256 = Convert.ToHexString(SHA256.HashData(leafPublic)),
    nameConstraintsDerHex = Convert.ToHexString(constraintBytes),
    permittedDnsNames = new[] {"localhost","127.0.0.1"}, permittedIp = "127.0.0.1/32",
    expiresUtc = expiry.ToString("O"), validityDays, rootPathLengthConstraint = 0,
    customRootChainPassed = verified.ok, nonLoopbackDnsRejected = !negative.ok, negative.statuses,
    localhostSanMatches = true, loopbackIpSanMatches = true,
    rootPrivateKeyPersisted = false, rootPrivateKeyExported = false,
    leafKeyStorage = "Encrypted PFX in current-user-only workspace directory",
    certificateStoreChanges = false, machineWideChanges = false
};
var json = JsonSerializer.Serialize(metadata,new JsonSerializerOptions {WriteIndented=true});
File.WriteAllText(Path.Combine(destination,"metadata.json"),json);
Console.WriteLine(json);

internal static class NativeStore
{
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CertOpenStore(IntPtr provider, uint encoding, IntPtr cryptProvider, uint flags, string name);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CertAddEncodedCertificateToStore(IntPtr store, uint encoding, byte[] encoded, uint size, uint disposition, IntPtr context);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CertControlStore(IntPtr store, uint flags, uint control, IntPtr parameter);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CertCloseStore(IntPtr store, uint flags);
}
