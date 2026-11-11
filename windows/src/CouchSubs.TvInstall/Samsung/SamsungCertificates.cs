// SPDX-License-Identifier: AGPL-3.0-only
// Samsung'un sertifika uç noktaları ve istek biçimleri Apps2Samsung'dan (MIT, Patrick Stel) alındı:
// https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CouchSubs.TvInstall.Samsung;

/// <summary>Bir TV için imza dosyaları: yazar ve dağıtıcı sertifikası (PKCS#12) ve parolaları.</summary>
public sealed record SigningProfile(string Directory, string AuthorP12, string DistributorP12, string Password)
{
    public static SigningProfile In(string directory) => new(directory,
        Path.Combine(directory, "author.p12"), Path.Combine(directory, "distributor.p12"),
        File.Exists(Path.Combine(directory, "password.txt")) ? File.ReadAllText(Path.Combine(directory, "password.txt")).Trim() : "");

    public bool Exists => File.Exists(AuthorP12) && File.Exists(DistributorP12) && Password.Length > 0;

    /// <summary>
    /// Yalnız tarihlere bakılır; anahtar diske yazılmasın diye geçici yüklenir. macOS EphemeralKeySet'i desteklemiyor
    /// (PlatformNotSupportedException): Mac'te kayıtlı sertifikayla her kurulum, yani TV uygulamasının her güncellemesi
    /// "This platform does not support loading with EphemeralKeySet" hatasıyla bitiyordu.
    /// </summary>
    internal static X509KeyStorageFlags KeyStorage =>
        OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    /// <summary>Sertifikalar hâlâ geçerli mi (bir gün pay bırakılarak)?</summary>
    public bool IsValid(DateTime now)
    {
        if (!Exists) return false;
        try
        {
            foreach (var path in new[] { AuthorP12, DistributorP12 })
            {
                using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, Password, KeyStorage);
                if (certificate.NotBefore > now || certificate.NotAfter < now.AddDays(1)) return false;
            }
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// Samsung'dan TV'ye özel imza sertifikası alır: yazar sertifikası Samsung hesabına, dağıtıcı sertifikası TV'nin
/// DUID'ine bağlıdır. Tizen Studio'nun Certificate Manager'ının yaptığının aynısıdır.
/// </summary>
public sealed class SamsungCertificates(HttpClient http)
{
    private const string AuthorUrl = "https://svdca.samsungqbe.com/apis/v3/authors";
    private const string DistributorProfileUrl = "https://svdca.samsungqbe.com/apis/v1/distributors";
    private const string DistributorUrl = "https://svdca.samsungqbe.com/apis/v3/distributors";

    /// <summary>Sertifikaları alır ve <paramref name="directory"/> içine yazar.</summary>
    public async Task<SigningProfile> CreateAsync(string duid, SamsungAccount account, string directory,
        CancellationToken cancellationToken = default)
    {
        using var key = RSA.Create(2048);
        var authorCsr = AuthorRequest(key);
        var distributorCsr = DistributorRequest(key, duid, account.Email);

        var author = await PostAsync(AuthorUrl, account, authorCsr, "author.csr", distributor: false, cancellationToken);
        // Eski TV'ler (Tizen 4 ve öncesi) için cihaz profili; yenilerde imzanın içinde olduğu için kullanılmaz ama saklanır.
        var deviceProfile = await PostAsync(DistributorProfileUrl, account, distributorCsr, "distributor.csr", distributor: true, cancellationToken);
        var distributor = await PostAsync(DistributorUrl, account, distributorCsr, "distributor.csr", distributor: true, cancellationToken);

        Directory.CreateDirectory(directory);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var authorPath = Path.Combine(directory, "author.p12");
        var distributorPath = Path.Combine(directory, "distributor.p12");
        await File.WriteAllBytesAsync(authorPath, Pkcs12(LoadCertificate(author), key, password), cancellationToken);
        await File.WriteAllBytesAsync(distributorPath, Pkcs12(LoadCertificate(distributor), key, password), cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directory, "device-profile.xml"), deviceProfile, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "password.txt"), password, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "duid.txt"), duid, cancellationToken);
        return new SigningProfile(directory, authorPath, distributorPath, password);
    }

    internal static byte[] AuthorRequest(RSA key)
    {
        var request = new CertificateRequest("CN=CouchSubs", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Encoding.ASCII.GetBytes(request.CreateSigningRequestPem());
    }

    internal static byte[] DistributorRequest(RSA key, string duid, string email)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("TizenSDK");
        if (!string.IsNullOrWhiteSpace(email)) subject.AddEmailAddress(email);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // TV, imzayı yalnız kendi DUID'i sertifikada yazılıysa kabul eder. Adlar olduğu gibi (büyük harfle) yazılmalı;
        // SubjectAlternativeNameBuilder.AddUri şemayı küçük harfe çevirdiği için ASN.1 elle yazılır.
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var name in new[] { "URN:tizen:packageid=", $"URN:tizen:deviceid={duid}" })
                writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 6));
        }
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", writer.Encode(), critical: false));
        return Encoding.ASCII.GetBytes(request.CreateSigningRequestPem());
    }

    private async Task<byte[]> PostAsync(string url, SamsungAccount account, byte[] csr, string fileName, bool distributor,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(account.AccessToken), "access_token" },
            { new StringContent(account.UserId), "user_id" },
            { new StringContent("VD"), "platform" },
        };
        if (distributor)
        {
            content.Add(new StringContent("public"), "privilege_level");
            content.Add(new StringContent("Individual"), "developer_type");
        }
        content.Add(new ByteArrayContent(csr), "csr", fileName);
        using var response = await http.PostAsync(url, content, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SamsungInstallException(SamsungInstallError.CertificateFailed,
                $"{(int)response.StatusCode} {Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 300)))}");
        return body;
    }

    internal static X509Certificate2 LoadCertificate(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data);
        return text.Contains("-----BEGIN CERTIFICATE-----")
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificate(data);
    }

    /// <summary>
    /// Sertifika, anahtar ve onu veren Samsung ara sertifikası tek PKCS#12 dosyasında. Şifreleme 3DES: her platformun
    /// okuyabildiği biçim.
    /// </summary>
    internal static byte[] Pkcs12(X509Certificate2 leaf, RSA key, string password)
    {
        using var withKey = leaf.CopyWithPrivateKey(key);
        var chain = new X509Certificate2Collection { withKey };
        if (Intermediate(leaf) is { } intermediate) chain.Add(intermediate);
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 2048);
        return chain.ExportPkcs12(parameters, password);
    }

    private static X509Certificate2? Intermediate(X509Certificate2 leaf) =>
        SamsungCa.All().FirstOrDefault(ca => ca.Subject == leaf.Issuer && ca.Subject != ca.Issuer);
}

/// <summary>Samsung'un geliştirici sertifikalarını veren ara sertifikalar (Tizen Studio'dan, değiştirilmeden).</summary>
internal static class SamsungCa
{
    private static readonly string[] Names = ["vd_tizen_dev_author_ca", "author_ca", "vd_tizen_dev_public2", "public2"];

    public static IEnumerable<X509Certificate2> All()
    {
        var assembly = typeof(SamsungCa).Assembly;
        foreach (var name in Names)
        {
            using var stream = assembly.GetManifestResourceStream($"SamsungCa.{name}.pem");
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return X509Certificate2.CreateFromPem(reader.ReadToEnd());
        }
    }
}
