using System.Reflection;

namespace Wrapp;

/// <summary>
/// Single source of truth for application identity strings.
/// Version is read from the assembly's InformationalVersion attribute
/// (populated by MSBuild from VersionPrefix + VersionSuffix in the csproj).
/// </summary>
public static class AppInfo
{
    public const string Name = "Wrapp";

    /// <summary>Semantic version including suffix, e.g. "0.1.0-beta".</summary>
    public static string Version { get; } = Assembly
        .GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        // Strip the +commitHash suffix that SDK-style projects may append
        ?.Split('+')[0]
        ?? "0.0.0";

    /// <summary>"v0.1.0-beta"</summary>
    public static string VersionDisplay => $"v{Version}";

    /// <summary>"Wrapp v0.1.0-beta"</summary>
    public static string NameAndVersion => $"{Name} {VersionDisplay}";
}
