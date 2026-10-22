using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Wrapp.Services;

namespace Wrapp.Tests;

public class IntuneWinServiceTests : IDisposable
{
    private readonly string _root;

    public IntuneWinServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "WrappIntuneWin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void InspectPackage_NonExistentFile_ReturnsNull()
    {
        var result = IntuneWinService.InspectPackage(@"C:\nonexistent\fake.intunewin");
        Assert.Null(result);
    }

    [Fact]
    public void InspectPackage_InvalidZip_ReturnsNull()
    {
        // Create a temp file that's not a ZIP
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.intunewin");
        try
        {
            File.WriteAllText(path, "not a zip file");
            var result = IntuneWinService.InspectPackage(path);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateDecryptedFile_NonExistent_ReturnsFalse()
    {
        Assert.False(IntuneWinService.ValidateDecryptedFile(@"C:\nonexistent\fake.exe"));
    }

    [Fact]
    public void ValidateDecryptedFile_EmptyFile_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.bin");
        try
        {
            File.Create(path).Dispose();
            Assert.False(IntuneWinService.ValidateDecryptedFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateDecryptedFile_ZipSignature_ReturnsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.bin");
        try
        {
            // PK signature (ZIP)
            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
            Assert.True(IntuneWinService.ValidateDecryptedFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateDecryptedFile_MzSignature_ReturnsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.bin");
        try
        {
            // MZ signature (EXE)
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 });
            Assert.True(IntuneWinService.ValidateDecryptedFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecryptAsync_InvalidKey_ReturnsFalse()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"enc_{Guid.NewGuid()}.bin");
        var outputPath = Path.Combine(Path.GetTempPath(), $"dec_{Guid.NewGuid()}.bin");
        try
        {
            // Create a fake encrypted file (48-byte header + some data)
            var fakeData = new byte[100];
            new Random(42).NextBytes(fakeData);
            await File.WriteAllBytesAsync(sourcePath, fakeData);

            // Use a valid-format but wrong key
            var fakeKey = Convert.ToBase64String(new byte[32]); // 256-bit zero key
            var fakeIV = Convert.ToBase64String(new byte[16]);  // 128-bit zero IV

            var result = await IntuneWinService.DecryptAsync(sourcePath, outputPath, fakeKey, fakeIV);
            Assert.False(result);
            Assert.False(File.Exists(outputPath)); // cleaned up on failure
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // ── Real-crypto fixtures: exercise the AES decrypt path itself ──
    // The InvalidKey test above feeds 100 random bytes, so it fails at the
    // header/garbage stage and never reaches the AES block decrypt. These
    // build a genuinely encrypted blob (the inverse of what DecryptAsync
    // does: AES-256-CBC/PKCS7 ciphertext behind a 48-byte HMAC+IV header)
    // so a regression that broke the actual decrypt -- not just header
    // validation -- is caught.

    private static (byte[] key, byte[] iv) NewKeyIv(int seed)
    {
        var rng = new Random(seed);
        var key = new byte[32]; rng.NextBytes(key);   // AES-256
        var iv  = new byte[16]; rng.NextBytes(iv);
        return (key, iv);
    }

    private static byte[] AesCbcEncrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();           // default Mode=CBC, Padding=PKCS7
        aes.Key = key;
        aes.IV  = iv;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plain, 0, plain.Length);
    }

    /// <summary>
    /// Builds the Intune blob layout [HMAC-SHA256(32)][IV(16)][ciphertext].
    /// The HMAC covers IV+ciphertext keyed by <paramref name="macKey"/> - a real
    /// MAC so the SEC-7 integrity check in DecryptAsync passes for valid content
    /// (and would fail if the ciphertext were tampered). Returns the blob; the
    /// 32-byte MAC prefix is <c>blob[0..32]</c>.
    /// </summary>
    private static byte[] BuildEncryptedBlob(byte[] plain, byte[] key, byte[] iv, byte[] macKey)
    {
        var cipher = AesCbcEncrypt(plain, key, iv);
        var body = new byte[iv.Length + cipher.Length];     // IV + ciphertext = HMAC input
        Buffer.BlockCopy(iv, 0, body, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, body, iv.Length, cipher.Length);

        using var hmac = new HMACSHA256(macKey);
        var mac = hmac.ComputeHash(body);                   // 32 bytes

        var blob = new byte[32 + body.Length];
        Buffer.BlockCopy(mac, 0, blob, 0, 32);
        Buffer.BlockCopy(body, 0, blob, 32, body.Length);
        return blob;
    }

    [Fact]
    public async Task DecryptAsync_CorrectKey_RoundTripsToOriginalPlaintext()
    {
        var (key, iv) = NewKeyIv(1);
        var plain = new byte[5000];
        new Random(2).NextBytes(plain);
        plain[0] = 0x50; plain[1] = 0x4B;                   // make it look like a ZIP (PK)

        var src = Path.Combine(_root, "blob.bin");
        var dst = Path.Combine(_root, "out.bin");
        await File.WriteAllBytesAsync(src, BuildEncryptedBlob(plain, key, iv, new byte[32]));

        var ok = await IntuneWinService.DecryptAsync(
            src, dst, Convert.ToBase64String(key), Convert.ToBase64String(iv));

        Assert.True(ok);
        Assert.Equal(plain, await File.ReadAllBytesAsync(dst));   // byte-for-byte recovery
    }

    [Fact]
    public async Task DecryptAsync_WrongKey_FailsPaddingAndDeletesOutput()
    {
        var (key, iv) = NewKeyIv(3);
        var plain = new byte[256];
        new Random(4).NextBytes(plain);

        var src = Path.Combine(_root, "blob2.bin");
        var dst = Path.Combine(_root, "out2.bin");
        await File.WriteAllBytesAsync(src, BuildEncryptedBlob(plain, key, iv, new byte[32]));

        // Flip one key byte: AES decrypt yields garbage and PKCS7 padding
        // validation throws CryptographicException -> false, output removed.
        var wrongKey = (byte[])key.Clone();
        wrongKey[0] ^= 0xFF;

        var ok = await IntuneWinService.DecryptAsync(
            src, dst, Convert.ToBase64String(wrongKey), Convert.ToBase64String(iv));

        Assert.False(ok);
        Assert.False(File.Exists(dst));
    }

    // ── End-to-end: build a real .intunewin and run the full pipeline ──

    private string BuildIntunewinPackage(byte[] key, byte[] iv, byte[] plain, string setupFile, string appName)
    {
        var macKey = new byte[32]; new Random(77).NextBytes(macKey);
        var blob = BuildEncryptedBlob(plain, key, iv, macKey);
        var mac  = blob[..32];                               // the real HMAC prefix
        var path = Path.Combine(_root, "pkg.intunewin");

        var detectionXml = $"""
            <ApplicationInfo ToolVersion="1.0">
              <Name>{appName}</Name>
              <SetupFile>{setupFile}</SetupFile>
              <FileName>{setupFile}.intunewin</FileName>
              <UnencryptedContentSize>{plain.Length}</UnencryptedContentSize>
              <EncryptionInfo>
                <EncryptionKey>{Convert.ToBase64String(key)}</EncryptionKey>
                <MacKey>{Convert.ToBase64String(macKey)}</MacKey>
                <InitializationVector>{Convert.ToBase64String(iv)}</InitializationVector>
                <Mac>{Convert.ToBase64String(mac)}</Mac>
                <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
                <FileDigest>{Convert.ToBase64String(new byte[32])}</FileDigest>
                <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
              </EncryptionInfo>
            </ApplicationInfo>
            """;

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var det = zip.CreateEntry("IntuneWinPackage/Metadata/Detection.xml");
        using (var w = new StreamWriter(det.Open())) w.Write(detectionXml);
        var inner = zip.CreateEntry($"IntuneWinPackage/Contents/{setupFile}.intunewin");
        using (var s = inner.Open()) s.Write(blob, 0, blob.Length);
        return path;
    }

    [Fact]
    public void InspectPackage_ValidPackage_ReturnsPopulatedMetadata()
    {
        var (key, iv) = NewKeyIv(5);
        var path = BuildIntunewinPackage(key, iv, new byte[64], "setup.exe", "Contoso Reader");

        var info = IntuneWinService.InspectPackage(path);

        Assert.NotNull(info);
        Assert.Equal("Contoso Reader", info!.PackageName);
        Assert.Equal("setup.exe", info.SetupFile);
        Assert.Equal(Convert.ToBase64String(key), info.EncryptionKey);
        Assert.Equal(Convert.ToBase64String(iv), info.InitializationVector);
        Assert.Equal(64, info.UnencryptedContentSize);
    }

    [Fact]
    public async Task ExtractAndDecryptAsync_RealPackage_RecoversOriginalContent()
    {
        var (key, iv) = NewKeyIv(6);
        var plain = new byte[3000];
        new Random(8).NextBytes(plain);
        plain[0] = 0x4D; plain[1] = 0x5A;                   // MZ (EXE-shaped payload)

        var pkg = BuildIntunewinPackage(key, iv, plain, "installer.exe", "MyApp");
        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);

        var decrypted = await IntuneWinService.ExtractAndDecryptAsync(pkg, outDir);

        Assert.NotNull(decrypted);
        Assert.Equal("installer.exe", Path.GetFileName(decrypted!));
        Assert.Equal(plain, await File.ReadAllBytesAsync(decrypted));
    }
}
