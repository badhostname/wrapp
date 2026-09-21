namespace Wrapp.Services;

/// <summary>
/// <para>
/// Instance-facing platform path surface. Mirrors the public properties of
/// the static <see cref="PlatformConfig"/> so services / view-models that
/// take an <c>IEnvironmentConfig</c> via ctor resolve paths through the same
/// env-var / appsettings.json / default cascade as code that still calls
/// <c>PlatformConfig.LogPath</c> directly.
/// </para>
/// <para>
/// Exists for test mockability: services that currently reach into
/// <c>PlatformConfig</c> cannot be unit-tested without polluting real paths.
/// A test fake can redirect every path into a <c>Path.GetTempPath()</c>
/// sandbox and assert side effects without touching the user profile.
/// </para>
/// <para>
/// Migration note: new services should take <c>IEnvironmentConfig</c>
/// via constructor with <see cref="PlatformConfig.Instance"/> as the default
/// argument. Existing services can continue to use the static until touched
/// by a later refactor.
/// </para>
/// </summary>
public interface IEnvironmentConfig
{
    string LogPath       { get; }
    string LogDirectory  { get; }
    LogLevel LogLevel    { get; }
    string SettingsPath  { get; }
    string MsalCachePath { get; }
}

/// <summary>
/// Production <see cref="IEnvironmentConfig"/> - forwards every property to
/// the corresponding <see cref="PlatformConfig"/> static. A single shared
/// instance is exposed via <see cref="PlatformConfig.Instance"/>.
/// </summary>
internal sealed class DefaultEnvironmentConfig : IEnvironmentConfig
{
    public string LogPath       => PlatformConfig.LogPath;
    public string LogDirectory  => PlatformConfig.LogDirectory;
    public LogLevel LogLevel    => PlatformConfig.LogLevel;
    public string SettingsPath  => PlatformConfig.SettingsPath;
    public string MsalCachePath => PlatformConfig.MsalCachePath;
}
