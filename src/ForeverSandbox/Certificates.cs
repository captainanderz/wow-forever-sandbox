// Local login certificates: a loopback-only, name-constrained root and a leaf for 127.0.0.1/localhost, made per machine by
// tools/ForeverCertificates (the root key is destroyed right after issuing). Kept valid automatically: when the leaf has fewer
// than 14 days left a new pair is made and trusted, then the old pair this launcher added is removed. CurrentUser stores only;
// ownership is recorded before every change so only our own entries are ever removed.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ForeverSandbox;

internal sealed class Certificates
{
    private const string RootSubject = "CN=Forever Sandbox Loopback Test Root", LeafSubject = "CN=127.0.0.1";
    private const string LoopbackConstraints = "3028A026300B82096C6F63616C686F7374300B82093132372E302E302E31300A87087F000001FFFFFFFF";
    private const int RenewWithinDays = 14, ValidityDays = 365;

    private sealed class TrustState
    {
        public string RootThumbprint { get; set; }
        public string LeafThumbprint { get; set; }
        public bool RootAddedByLauncher { get; set; }
        public bool LeafAddedByLauncher { get; set; }
    }

    private readonly string folder, tool;
    public Certificates(string productRoot)
    {
        folder = Path.Combine(productRoot, "certificates");
        tool = Path.Combine(productRoot, "tools", "ForeverCertificates.exe");
    }

    private string Chain(string name) => Path.Combine(folder, name);
    private string StatePath(string name) => Path.Combine(folder, name + "-trust.json");

    // Returns the leaf thumbprint of a valid, trusted chain, renewing it first when needed.
    public string Ensure()
    {
        Directory.CreateDirectory(folder);
        if (Usable("chain", out var leaf)) return leaf;

        Console.WriteLine($"Setting up the local login certificates (valid {ValidityDays} days, localhost/127.0.0.1 only).");
        Console.WriteLine("Windows will ask you to ADD \"Forever Sandbox Loopback Test Root\"" + (File.Exists(StatePath("chain")) ? " and then to DELETE the old one" : "") + ". Answer Yes.");
        // An interrupted earlier renewal: undo what it trusted and discard it (our own material, never shared).
        if (File.Exists(StatePath("chain-next"))) Remove("chain-next");
        if (Directory.Exists(Chain("chain-next"))) Directory.Delete(Chain("chain-next"), true);

        Run(tool, Chain("chain-next"), "--days", ValidityDays.ToString());
        Trust("chain-next");
        if (!VerifyTrust(Chain("chain-next"))) throw new InvalidOperationException("The new certificates do not validate after trusting them.");
        if (File.Exists(StatePath("chain"))) Remove("chain");
        if (Directory.Exists(Chain("chain"))) Directory.Delete(Chain("chain"), true);
        Directory.Move(Chain("chain-next"), Chain("chain"));
        File.Move(StatePath("chain-next"), StatePath("chain"), true);
        if (!Usable("chain", out leaf)) throw new InvalidOperationException("The renewed certificates are not usable.");
        Console.WriteLine("Local login certificates ready.");
        return leaf;
    }

    private bool Usable(string name, out string leafThumbprint)
    {
        leafThumbprint = null;
        var metadata = Path.Combine(Chain(name), "metadata.json");
        if (!File.Exists(metadata) || !File.Exists(StatePath(name))) return false;
        using var root = new X509Certificate2(Path.Combine(Chain(name), "root.cer"));
        if (root.Subject != RootSubject) return false;   // an older certificate profile: replace it
        using var leaf = new X509Certificate2(Path.Combine(Chain(name), "leaf.cer"));
        if (leaf.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(RenewWithinDays)) return false;
        if (!VerifyTrust(Chain(name))) return false;
        leafThumbprint = leaf.Thumbprint;
        return true;
    }

    // Fresh-process Windows chain validation (the importer's cached chain engine is not reused).
    private bool VerifyTrust(string chain)
    {
        var output = Run(tool, "--verify-trust", chain, allowFailure: true);
        try { return JsonDocument.Parse(output).RootElement.GetProperty("passed").GetBoolean(); } catch (JsonException) { return false; }
    }

    private (X509Certificate2 root, X509Certificate2 leaf) Load(string name)
    {
        var dir = Chain(name);
        var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "metadata.json"))).RootElement;
        var rootBytes = File.ReadAllBytes(Path.Combine(dir, "root.cer")); var leafBytes = File.ReadAllBytes(Path.Combine(dir, "leaf.cer"));
        if (Convert.ToHexString(SHA256.HashData(rootBytes)) != metadata.GetProperty("rootPublicSha256").GetString()
            || Convert.ToHexString(SHA256.HashData(leafBytes)) != metadata.GetProperty("leafPublicSha256").GetString())
            throw new CryptographicException("Prepared certificates do not match their metadata.");
        var root = new X509Certificate2(rootBytes); var leaf = new X509Certificate2(leafBytes);
        var constraints = root.Extensions.Cast<X509Extension>().Where(e => e.Oid?.Value == "2.5.29.30").ToList();
        var rootBasic = root.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var leafBasic = leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (root.Thumbprint != metadata.GetProperty("rootThumbprint").GetString() || root.Subject != RootSubject || root.Issuer != RootSubject || root.HasPrivateKey
            || leaf.Thumbprint != metadata.GetProperty("leafThumbprint").GetString() || leaf.Subject != LeafSubject || leaf.Issuer != RootSubject
            || constraints.Count != 1 || !constraints[0].Critical || Convert.ToHexString(constraints[0].RawData) != LoopbackConstraints
            || rootBasic is not { CertificateAuthority: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 } || leafBasic is not { CertificateAuthority: false })
            throw new CryptographicException("Prepared certificates are not the expected loopback-only profile.");
        return (root, leaf);
    }

    private void Trust(string name)
    {
        var (root, leaf) = Load(name);
        using (root) using (leaf)
        {
            var state = new TrustState { RootThumbprint = root.Thumbprint, LeafThumbprint = leaf.Thumbprint };
            using var my = new X509Store(StoreName.My, StoreLocation.CurrentUser); my.Open(OpenFlags.ReadWrite);
            using var trusted = new X509Store(StoreName.Root, StoreLocation.CurrentUser); trusted.Open(OpenFlags.ReadWrite);
            if (my.Certificates.Find(X509FindType.FindByThumbprint, leaf.Thumbprint, false).Count == 0)
            {
                var password = File.ReadAllText(Path.Combine(Chain(name), "password.txt"));
                using var withKey = new X509Certificate2(Path.Combine(Chain(name), "leaf.pfx"), password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                if (withKey.Thumbprint != leaf.Thumbprint || !withKey.HasPrivateKey) throw new CryptographicException("The leaf key file does not hold the prepared leaf.");
                state.LeafAddedByLauncher = true; Save(name, state);   // intent first, so an interrupted import is removable
                my.Add(withKey);
            }
            if (trusted.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false).Count == 0)
            {
                state.RootAddedByLauncher = true; Save(name, state);
                try { trusted.Add(root); }   // Windows shows its root-trust confirmation here
                catch (CryptographicException) { throw new InvalidOperationException("Adding the local root was declined; the game cannot log in to the sandbox without it."); }
            }
            Save(name, state);
        }
    }

    // Removes only entries recorded as added by the launcher (works after expiry too).
    private void Remove(string name)
    {
        var state = JsonSerializer.Deserialize<TrustState>(File.ReadAllText(StatePath(name)));
        using (var my = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            my.Open(OpenFlags.ReadWrite);
            if (state.LeafAddedByLauncher)
                foreach (var cert in my.Certificates.Find(X509FindType.FindByThumbprint, state.LeafThumbprint, false))
                {
                    using (var key = cert.GetRSAPrivateKey())
                    {
                        if (key is RSACng cng) cng.Key.Delete();
                        else if (key is RSACryptoServiceProvider csp) { csp.PersistKeyInCsp = false; csp.Clear(); }
                    }
                    my.Remove(cert);
                }
        }
        using (var trusted = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
        {
            trusted.Open(OpenFlags.ReadWrite);
            if (state.RootAddedByLauncher)
                foreach (var cert in trusted.Certificates.Find(X509FindType.FindByThumbprint, state.RootThumbprint, false)) trusted.Remove(cert);   // Windows confirms
        }
        File.Delete(StatePath(name));
    }

    private void Save(string name, TrustState state)
    {
        File.WriteAllText(StatePath(name) + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(StatePath(name) + ".tmp", StatePath(name), true);
    }

    private static string Run(string exe, params string[] args) => Run(exe, args[0], args.Skip(1).ToArray(), false);
    private static string Run(string exe, string first, string second, bool allowFailure) => Run(exe, first, new[] { second }, allowFailure);
    private static string Run(string exe, string first, string[] rest, bool allowFailure)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(first); foreach (var a in rest) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException(Path.GetFileName(exe) + " timed out."); }
        if (process.ExitCode != 0 && !allowFailure) throw new InvalidOperationException(Path.GetFileName(exe) + " failed: " + error.Result.Split('\n').FirstOrDefault());
        return output.Result;
    }
}
