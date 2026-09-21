using Microsoft.Win32;

namespace Wrapp.Services.Policy;

/// <summary>
/// Production policy source: <c>HKLM/HKCU\SOFTWARE\Policies\Wrapp</c>
/// (+ <c>\Recommended</c>). The Windows convention - domain GPO, Intune
/// ADMX ingestion, and the offline Apply-WrappPolicy.ps1 script all write
/// these same keys; the app only ever reads them. Always
/// <see cref="RegistryView.Registry64"/> (house style, RegistryTreeNode)
/// so a 32-bit publish reads the same values.
/// </summary>
public sealed class RegistryPolicyStore : IPolicyStore
{
    internal const string RootPath = @"SOFTWARE\Policies\Wrapp";

    public IReadOnlyDictionary<string, object> Read(PolicyHive hive, bool recommended)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var baseHive = hive == PolicyHive.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(baseHive, RegistryView.Registry64);
            var path = recommended ? RootPath + @"\Recommended" : RootPath;
            using var root = baseKey.OpenSubKey(path, writable: false);
            if (root is null) return result;

            foreach (var name in root.GetValueNames())
            {
                if (name.Length == 0) continue;
                if (root.GetValue(name) is { } v) result[name] = v;
            }

            foreach (var sub in root.GetSubKeyNames())
            {
                // The mandatory root's Recommended subkey is its own tier,
                // not a value container of this one.
                if (!recommended && string.Equals(sub, "Recommended", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var subKey = root.OpenSubKey(sub, writable: false);
                if (subKey is null) continue;
                foreach (var name in subKey.GetValueNames())
                {
                    if (name.Length == 0) continue;
                    if (subKey.GetValue(name) is { } v) result[$"{sub}.{name}"] = v;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Policy: registry read failed ({hive}, recommended={recommended}): {ex.Message}");
        }
        return result;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ReadKeyedList(
        PolicyHive hive, string listName)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var baseHive = hive == PolicyHive.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var baseKey = RegistryKey.OpenBaseKey(baseHive, RegistryView.Registry64);
            using var listKey = baseKey.OpenSubKey($@"{RootPath}\{listName}", writable: false);
            if (listKey is null) return result;

            foreach (var entryName in listKey.GetSubKeyNames())
            {
                using var entryKey = listKey.OpenSubKey(entryName, writable: false);
                if (entryKey is null) continue;
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var valueName in entryKey.GetValueNames())
                {
                    if (valueName.Length == 0) continue;
                    if (entryKey.GetValue(valueName) is { } v) values[valueName] = v;
                }
                result[entryName] = values;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Policy: keyed-list read failed ({hive}, {listName}): {ex.Message}");
        }
        return result;
    }
}

/// <summary>Test double: dictionaries per (hive, tier) plus keyed lists.</summary>
public sealed class InMemoryPolicyStore : IPolicyStore
{
    public Dictionary<string, object> MachineMandatory { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> UserMandatory { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> MachineRecommended { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> UserRecommended { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>listName → entryKey → values (mandatory tier).</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, object>>> MachineLists { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, Dictionary<string, object>>> UserLists { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object> Read(PolicyHive hive, bool recommended) => (hive, recommended) switch
    {
        (PolicyHive.Machine, false) => MachineMandatory,
        (PolicyHive.User, false) => UserMandatory,
        (PolicyHive.Machine, true) => MachineRecommended,
        _ => UserRecommended,
    };

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ReadKeyedList(
        PolicyHive hive, string listName)
    {
        var source = hive == PolicyHive.Machine ? MachineLists : UserLists;
        if (!source.TryGetValue(listName, out var entries))
            return new Dictionary<string, IReadOnlyDictionary<string, object>>();
        return entries.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, object>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
