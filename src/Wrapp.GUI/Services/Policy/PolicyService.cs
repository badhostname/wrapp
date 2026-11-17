using Wrapp.Models;

namespace Wrapp.Services.Policy;

/// <summary>
/// The resolved policy state for this launch. Built once at startup
/// (restart-to-apply model - AppSettings has no change notification, and
/// gpupdate + relaunch is the honest contract). Precedence inside a tier:
/// Machine wins over User.
/// </summary>
public sealed class PolicySnapshot
{
    /// <summary>Catalog key → enforced value. A mandatory value IS the lock.</summary>
    public IReadOnlyDictionary<string, object> Mandatory { get; init; } =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catalog key → recommended default (applied only onto factory values).</summary>
    public IReadOnlyDictionary<string, object> Recommended { get; init; } =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catalog key → "Machine policy" / "User policy" (mandatory tier).</summary>
    public IReadOnlyDictionary<string, string> SourceByKey { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> HiddenSections { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> HiddenSettingsTabs { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> HiddenSettings { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string? OrgDefaultsPath { get; init; }
    public string? ThemeFilePath { get; init; }
    public bool DisableSettingsImport { get; init; }
    public bool DisableOrgDefaultsImport { get; init; }

    /// <summary>Keyed entry lists (mandatory): entry Key → value name → value.
    /// Merged by Key on apply - policy entries win per-key, the user's own
    /// additional entries survive. Client secrets can never be provisioned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> TenantEntries { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> SiteEntries { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> DomainEntries { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Policy-provisioned custom placeholders (always non-sensitive -
    /// sensitive values are per-user DPAPI and cannot come from a machine key).</summary>
    public IReadOnlyDictionary<string, string> Placeholders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extra log-redaction regexes, merged with the org file's.</summary>
    public IReadOnlyList<string> RedactionPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Keys mandated from the MACHINE hive specifically - the only
    /// tier that can bypass per-user TOFU approval (writing HKLM requires
    /// local admin, a stronger authority than the user's own DPAPI token;
    /// HKCU is writable by the user's processes and never bypasses).</summary>
    public IReadOnlySet<string> MachineMandatedKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool AnyManaged =>
        Mandatory.Count > 0 || HiddenSections.Count > 0 || HiddenSettingsTabs.Count > 0
        || HiddenSettings.Count > 0 || DisableSettingsImport || DisableOrgDefaultsImport
        || OrgDefaultsPath is not null || ThemeFilePath is not null
        || TenantEntries.Count > 0 || SiteEntries.Count > 0 || DomainEntries.Count > 0
        || Placeholders.Count > 0 || RedactionPatterns.Count > 0;

    public bool IsManaged(string key) => Mandatory.ContainsKey(key);
    public bool IsHidden(string key) => HiddenSettings.Contains(key);
    public bool IsTabHidden(string tab) => HiddenSettingsTabs.Contains(tab);
    public bool IsSectionHidden(NavigationSection section) => HiddenSections.Contains(section.ToString());

    /// <summary>True when <paramref name="key"/> is machine-mandated to exactly
    /// <paramref name="value"/> (ordinal, trimmed) - the TOFU-bypass test.</summary>
    public bool MachineMandatedEquals(string key, string? value)
        => MachineMandatedKeys.Contains(key)
           && Mandatory.TryGetValue(key, out var v)
           && string.Equals(v as string ?? v.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stable content digest of everything policy controls. Two snapshots
    /// with the same fingerprint are behaviorally identical - the change
    /// monitor compares a freshly-read snapshot's fingerprint against the
    /// launch one to detect an external policy change.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        foreach (var kv in Mandatory.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Line($"M|{kv.Key}={kv.Value}|{SourceByKey.GetValueOrDefault(kv.Key)}");
        foreach (var kv in Recommended.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Line($"R|{kv.Key}={kv.Value}");
        foreach (var s in HiddenSections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Line($"HS|{s}");
        foreach (var s in HiddenSettingsTabs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Line($"HT|{s}");
        foreach (var s in HiddenSettings.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Line($"HI|{s}");
        Line($"ODP|{OrgDefaultsPath}"); Line($"TFP|{ThemeFilePath}");
        Line($"DSI|{DisableSettingsImport}"); Line($"DOI|{DisableOrgDefaultsImport}");
        foreach (var (name, lists) in new[] { ("T", TenantEntries), ("S", SiteEntries), ("D", DomainEntries) })
            foreach (var entry in lists.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var v in entry.Value.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
                    Line($"L{name}|{entry.Key}|{v.Key}={(v.Value is string[] arr ? string.Join(";", arr) : v.Value)}");
        foreach (var kv in Placeholders.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Line($"P|{kv.Key}={kv.Value}");
        foreach (var p in RedactionPatterns.OrderBy(x => x, StringComparer.Ordinal)) Line($"X|{p}");

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>
/// Reads administrator policy (see <see cref="RegistryPolicyStore"/>) and
/// applies it over <see cref="AppSettings"/>. Ordering at startup is
/// load-bearing: Load → ApplyRecommended → org-defaults seeding →
/// ApplyMandatory. Recommended fills factory values before the org file can
/// (policy > org file); mandatory runs LAST and unconditionally, so the
/// seeder needs no policy awareness at all. Mandatory values write through
/// to settings.json on the next save - required by the dirty-diff tracker,
/// and it means a lifted policy leaves the last org value in place.
/// </summary>
public static class PolicyService
{
    private static IPolicyStore _store = new RegistryPolicyStore();

    /// <summary>Test seam. Pass null to restore the registry store.</summary>
    internal static void OverrideStore(IPolicyStore? store)
    {
        _store = store ?? new RegistryPolicyStore();
        _current = null;
    }

    private static PolicySnapshot? _current;

    /// <summary>The snapshot for this launch (built on first access).</summary>
    public static PolicySnapshot Current => _current ??= Build();

    /// <summary>Reads the store fresh WITHOUT replacing the launch snapshot -
    /// the running app stays on the launch policy (restart-to-apply); the
    /// change monitor uses this to detect drift.</summary>
    internal static PolicySnapshot BuildFresh() => Build();

    /// <summary>Set by the change monitor when the registry policy no longer
    /// matches the launch snapshot. Drives the PolicyChangedGate.</summary>
    public static volatile bool ChangedSinceLaunch;

    private static PolicySnapshot Build()
    {
        var machine = _store.Read(PolicyHive.Machine, recommended: false);
        var user = _store.Read(PolicyHive.User, recommended: false);
        var machineRec = _store.Read(PolicyHive.Machine, recommended: true);
        var userRec = _store.Read(PolicyHive.User, recommended: true);

        var mandatory = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var machineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hiddenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hiddenTabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hiddenSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var redactionPatterns = new List<string>();
        string? orgDefaultsPath = null, themeFilePath = null;
        bool disableImport = false, disableOrgImport = false;

        // User first, machine second: machine overwrites on conflict.
        foreach (var (raw, isMachine) in new[] { (user, false), (machine, true) })
        {
            foreach (var (name, value) in raw)
            {
                if (TryFlag(name, "HiddenSections", value, hiddenSections)) continue;
                if (TryFlag(name, "HiddenSettingsTabs", value, hiddenTabs)) continue;
                if (TryFlag(name, "HiddenSettings", value, hiddenSettings)) continue;

                if (name.StartsWith("Placeholders.", StringComparison.OrdinalIgnoreCase))
                {
                    var phName = name["Placeholders.".Length..];
                    if (value is string phValue && PlaceholderService.IsValidCustomName(phName))
                        placeholders[phName] = phValue;
                    else
                        AppLogger.Warn($"Policy: placeholder '{phName}' invalid (reserved name or non-string) - ignored");
                    continue;
                }
                if (name.StartsWith("RedactionPatterns.", StringComparison.OrdinalIgnoreCase))
                {
                    if (value is string pattern && pattern.Length > 0 && !redactionPatterns.Contains(pattern))
                        redactionPatterns.Add(pattern);
                    continue;
                }

                switch (name)
                {
                    case "OrgDefaultsPath": orgDefaultsPath = value as string; continue;
                    case "ThemeFilePath": themeFilePath = value as string; continue;
                    case "DisableSettingsImport": disableImport = ToBool(value); continue;
                    case "DisableOrgDefaultsImport": disableOrgImport = ToBool(value); continue;
                }

                if (Coerce(name, value) is not { } typed) continue;
                mandatory[name] = typed;
                sources[name] = isMachine ? "Machine policy" : "User policy";
                if (isMachine) machineKeys.Add(name);
                else machineKeys.Remove(name);
            }
        }

        var recommended = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[] { userRec, machineRec }) // machine wins
            foreach (var (name, value) in raw)
                if (Coerce(name, value) is { } typed)
                    recommended[name] = typed;

        // Settings and General can never be hidden - the operator must always
        // be able to reach the managed-policy surface and a home view.
        foreach (var required in new[] { nameof(NavigationSection.Settings), nameof(NavigationSection.General) })
            if (hiddenSections.Remove(required))
                AppLogger.Warn($"Policy: HiddenSections cannot hide '{required}' - ignored");

        var snapshot = new PolicySnapshot
        {
            Mandatory = mandatory,
            Recommended = recommended,
            SourceByKey = sources,
            MachineMandatedKeys = machineKeys,
            HiddenSections = hiddenSections,
            HiddenSettingsTabs = hiddenTabs,
            HiddenSettings = hiddenSettings,
            OrgDefaultsPath = orgDefaultsPath,
            ThemeFilePath = themeFilePath,
            DisableSettingsImport = disableImport,
            DisableOrgDefaultsImport = disableOrgImport,
            Placeholders = placeholders,
            RedactionPatterns = redactionPatterns,
            TenantEntries = MergeKeyedList("IntuneTenants"),
            SiteEntries = MergeKeyedList("SccmSites"),
            DomainEntries = MergeKeyedList("Domains"),
        };

        if (snapshot.AnyManaged)
            AppLogger.Info($"Policy: {mandatory.Count} mandated value(s), {recommended.Count} recommended, " +
                           $"{hiddenSections.Count} hidden section(s), {hiddenTabs.Count} hidden tab(s)");
        return snapshot;
    }

    /// <summary>User entries first, machine entries overwrite per Key.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> MergeKeyedList(string listName)
    {
        var merged = new Dictionary<string, IReadOnlyDictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { PolicyHive.User, PolicyHive.Machine })
            foreach (var (key, values) in _store.ReadKeyedList(hive, listName))
                merged[key] = values;
        return merged;
    }

    private static bool TryFlag(string name, string prefix, object value, HashSet<string> into)
    {
        if (!name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) return false;
        if (ToBool(value)) into.Add(name[(prefix.Length + 1)..]);
        return true;
    }

    /// <summary>
    /// Validates + types one raw registry value against the catalog. Unknown
    /// names, wrong types, out-of-set enum strings, and unsafe URLs are
    /// IGNORED with a log line - a malformed policy must never take effect
    /// half-way.
    /// </summary>
    private static object? Coerce(string name, object raw)
    {
        var def = PolicyCatalog.Find(name);
        if (def is null)
        {
            AppLogger.Warn($"Policy: unknown value '{name}' ignored");
            return null;
        }

        object? typed = def.Kind switch
        {
            PolicyValueKind.String => raw as string,
            PolicyValueKind.Bool => raw is int i ? i != 0 : raw as bool?,
            PolicyValueKind.Int => raw as int?,
            _ => null,
        };
        if (typed is null)
        {
            AppLogger.Warn($"Policy: value '{name}' has the wrong type ({raw.GetType().Name}) - ignored");
            return null;
        }

        if (def.AllowedValues is { } allowed && typed is string s
            && !allowed.Contains(s, StringComparer.OrdinalIgnoreCase))
        {
            AppLogger.Warn($"Policy: '{name}' value '{s}' is not one of [{string.Join(", ", allowed)}] - ignored");
            return null;
        }

        // Security validation matches (and for the vault URL, exceeds) what
        // user input gets: a policy channel must not smuggle in a URL the UI
        // would reject.
        if (string.Equals(name, "UpdateFeedUrl", StringComparison.OrdinalIgnoreCase)
            && !UpdateService.IsValidFeedUrl(typed as string))
        {
            AppLogger.Warn("Policy: UpdateFeedUrl is not https/UNC/local - ignored");
            return null;
        }
        if (string.Equals(name, "KeyVaultRepoUrl", StringComparison.OrdinalIgnoreCase)
            && typed is string vault && vault.Length > 0
            && !(Uri.TryCreate(vault, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
        {
            AppLogger.Warn("Policy: KeyVaultRepoUrl is not https - ignored");
            return null;
        }

        return typed;
    }

    private static bool ToBool(object value) => value is int i ? i != 0 : value is bool b && b;

    // -------------------------------------------------------------------
    // Application
    // -------------------------------------------------------------------

    /// <summary>Seeds recommended values onto settings still at factory default
    /// (same semantics as org-defaults seeding; the user's edit wins).</summary>
    public static void ApplyRecommended(AppSettings settings)
    {
        foreach (var (key, value) in Current.Recommended)
        {
            var current = PolicyCatalog.GetValue(settings, key);
            var factory = PolicyCatalog.GetFactoryValue(key);
            if (!Equals(current, factory)) continue;
            if (Equals(current, value)) continue;
            if (PolicyCatalog.SetValue(settings, key, value))
                AppLogger.Info($"Policy: recommended '{key}' applied");
        }
    }

    /// <summary>Unconditionally enforces every mandated value, list entry and
    /// placeholder. Called after load/seeding, before every save (and again
    /// after the preferences grids persist), after a settings import, and
    /// after an org-defaults import. Returns true when anything was snapped
    /// back so callers can re-persist.</summary>
    public static bool ApplyMandatory(AppSettings settings)
    {
        var changed = false;
        foreach (var (key, value) in Current.Mandatory)
        {
            var current = PolicyCatalog.GetValue(settings, key);
            if (Equals(current, value)) continue;
            if (PolicyCatalog.SetValue(settings, key, value))
            {
                changed = true;
                AppLogger.Info($"Policy: mandated '{key}' enforced ({Current.SourceByKey.GetValueOrDefault(key)})");
            }
        }

        // Keyed lists: policy entries are merged BY KEY - a policy entry wins
        // for its key, the user's other entries survive. The subkey name IS
        // the entry's Key property.
        changed |= ApplyKeyedList(Current.TenantEntries, settings.IntuneTenants,
            t => t.Key, key => new SavedTenantEntry { Key = key }, "tenant");
        changed |= ApplyKeyedList(Current.SiteEntries, settings.SccmSites,
            s => s.Key, key => new SavedSiteEntry { Key = key }, "site");
        changed |= ApplyKeyedList(Current.DomainEntries, settings.Domains,
            d => d.Key, key => new SavedDomainEntry { Key = key }, "domain");

        // Placeholders: upsert as NON-SENSITIVE. A user's sensitive
        // placeholder of the same name is never converted or overwritten -
        // its plaintext lives in the per-user DPAPI sidecar, which a machine
        // policy has no authority over.
        foreach (var (name, value) in Current.Placeholders)
        {
            var existing = settings.Placeholders.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.Placeholders.Add(new PlaceholderEntry { Name = name, Value = value, IsSensitive = false });
                AppLogger.Info($"Policy: placeholder '{name}' provisioned");
                changed = true;
            }
            else if (existing.IsSensitive)
            {
                AppLogger.Warn($"Policy: placeholder '{name}' collides with a sensitive user placeholder - skipped");
            }
            else if (!string.Equals(existing.Value, value, StringComparison.Ordinal))
            {
                existing.Value = value;
                AppLogger.Info($"Policy: placeholder '{name}' enforced");
                changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyKeyedList<T>(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> policyEntries,
        List<T> target, Func<T, string> keyOf, Func<string, T> create, string label) where T : class
    {
        var changed = false;
        foreach (var (entryKey, values) in policyEntries)
        {
            var entry = target.FirstOrDefault(
                e => string.Equals(keyOf(e), entryKey, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = create(entryKey);
                target.Add(entry);
                AppLogger.Info($"Policy: {label} '{entryKey}' provisioned");
                changed = true;
            }
            changed |= ApplyEntryValues(entry, values, label, entryKey);
        }
        return changed;
    }

    private static bool ApplyEntryValues<T>(
        T entry, IReadOnlyDictionary<string, object> values, string label, string entryKey) where T : class
    {
        var changed = false;
        var type = entry.GetType();
        foreach (var (name, raw) in values)
        {
            // Secrets are per-user DPAPI ciphertext - a plaintext registry
            // value can never round-trip DecryptAuthentic, and provisioning
            // one would put a secret in a world-readable hive. Hard refusal.
            if (string.Equals(name, "ClientSecret", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warn($"Policy: {label} '{entryKey}' tried to set ClientSecret - refused");
                continue;
            }

            var prop = type.GetProperty(name);
            if (prop is null || !prop.CanWrite)
            {
                AppLogger.Warn($"Policy: {label} '{entryKey}' value '{name}' is unknown - ignored");
                continue;
            }

            try
            {
                object? desired = null;
                var supported = true;
                if (prop.PropertyType == typeof(string) && raw is string s)
                    desired = s;
                else if (prop.PropertyType == typeof(bool))
                    desired = raw is int i ? i != 0 : raw is bool b && b;
                else if (prop.PropertyType == typeof(int) && raw is int n)
                    desired = n;
                else if (prop.PropertyType.IsEnum && raw is string es
                         && Enum.TryParse(prop.PropertyType, es, ignoreCase: true, out var parsed))
                    desired = parsed;
                else if (prop.PropertyType == typeof(List<string>))
                    desired = raw switch
                    {
                        string[] arr => arr.ToList(),           // REG_MULTI_SZ
                        string one => one.Split(';', StringSplitOptions.RemoveEmptyEntries
                                                     | StringSplitOptions.TrimEntries).ToList(),
                        _ => new List<string>(),
                    };
                else
                    supported = false;

                if (!supported)
                {
                    AppLogger.Warn($"Policy: {label} '{entryKey}' value '{name}' has an unsupported type - ignored");
                    continue;
                }

                var current = prop.GetValue(entry);
                var equal = desired is List<string> list && current is List<string> cur
                    ? list.SequenceEqual(cur, StringComparer.Ordinal)
                    : Equals(current, desired);
                if (!equal)
                {
                    prop.SetValue(entry, desired);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Policy: {label} '{entryKey}' value '{name}' failed to apply: {ex.Message}");
            }
        }
        return changed;
    }
}
