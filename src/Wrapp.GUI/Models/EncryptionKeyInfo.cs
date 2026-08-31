namespace Wrapp.Models;

/// <summary>
/// Encryption key metadata for an Intune Win32 app's .intunewin content.
/// Keys are captured during upload and stored for later decryption during clone.
/// Includes all fields from detection.xml's ApplicationInfo + EncryptionInfo.
/// </summary>
public class EncryptionKeyInfo
{
    // Identity
    public string AppId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string TenantId { get; init; } = "";

    // Encryption keys (from EncryptionInfo)
    public string EncryptionKey { get; init; } = "";
    public string InitializationVector { get; init; } = "";
    public string MacKey { get; init; } = "";
    public string Mac { get; init; } = "";
    public string ProfileIdentifier { get; init; } = "ProfileVersion1";

    // File hashes
    public string FileDigest { get; init; } = "";            // SHA256 hash of the encrypted content
    public string FileDigestAlgorithm { get; init; } = "SHA256";

    // Package metadata (from ApplicationInfo)
    public string PackageName { get; init; } = "";            // ApplicationInfo.Name
    public string SetupFile { get; init; } = "";              // ApplicationInfo.SetupFile (original installer filename)
    public string InnerFileName { get; init; } = "";          // ApplicationInfo.FileName (encrypted inner file name)
    public long UnencryptedContentSize { get; init; }         // ApplicationInfo.UnencryptedContentSize

    // Source
    public string SourcePath { get; init; } = "";             // Full resolved path of the .intunewin file

    // Audit
    public string SavedAt { get; init; } = "";
    public string SavedBy { get; init; } = "";
}
