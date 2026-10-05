using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Palwyn.Linux;

/// <summary>
/// This PC's identity on Linux (docs/security.md section 3): a P-256 key in a file only this user can read (0600,
/// like an ssh key) plus its self-signed certificate. Weaker than Windows' non-exportable CNG key: anyone who can
/// read the user's files can copy it.
/// </summary>
public static class IdentityFile
{
    public static X509Certificate2 Load(string folder)
    {
        var keyPath = Path.Combine(folder, "identity.key");
        var certPath = Path.Combine(folder, "identity.cer");
        if (!File.Exists(keyPath) || !File.Exists(certPath)) Create(folder, keyPath, certPath);
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        return X509CertificateLoader.LoadCertificate(File.ReadAllBytes(certPath)).CopyWithPrivateKey(key);
    }

    static void Create(string folder, string keyPath, string certPath)
    {
        Directory.CreateDirectory(folder);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var cert = new CertificateRequest("CN=Palwyn", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        // Private from the moment it exists: the mode is set at creation, never changed after writing.
        File.Delete(keyPath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var w = new StreamWriter(keyPath, options)) w.Write(key.ExportPkcs8PrivateKeyPem());
        File.WriteAllBytes(certPath, cert.Export(X509ContentType.Cert));
    }
}
