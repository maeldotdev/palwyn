using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Windows.Storage;

namespace Palwyn.App.Link;

/// <summary>
/// This PC's identity (docs/security.md section 3): a named, non-exportable CNG P-256 key (SChannel needs a
/// persisted key) plus the self-signed certificate for it. The certificate is public and kept as a file.
/// </summary>
static class IdentityStore
{
    const string KeyName = "Palwyn.Identity";
    static string CertPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "identity.cer");

    public static X509Certificate2 Load()
    {
        if (!CngKey.Exists(KeyName) || !File.Exists(CertPath)) Create();
        var key = new ECDsaCng(CngKey.Open(KeyName));
        return X509CertificateLoader.LoadCertificate(File.ReadAllBytes(CertPath)).CopyWithPrivateKey(key);
    }

    static void Create()
    {
        var key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
        });
        using var ecdsa = new ECDsaCng(key);
        using var cert = new CertificateRequest("CN=Palwyn", ecdsa, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Cert));
        Log.Info("Created a new PC identity");
    }
}
