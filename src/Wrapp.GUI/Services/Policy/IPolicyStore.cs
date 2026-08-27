namespace Wrapp.Services.Policy;

/// <summary>The two policy hives, highest authority first.</summary>
public enum PolicyHive { Machine, User }

/// <summary>
/// Read abstraction over the policy source so <see cref="PolicyService"/> is
/// unit-testable without touching HKLM. The production implementation is
/// <see cref="RegistryPolicyStore"/>; tests use <see cref="InMemoryPolicyStore"/>.
/// </summary>
public interface IPolicyStore
{
    /// <summary>
    /// All values under the given hive's policy root (or its
    /// <c>Recommended</c> subkey), flattened: direct values by name, one
    /// level of subkeys as <c>Subkey.ValueName</c> (block leaves, the
    /// <c>Hidden*</c> lists, <c>Placeholders</c>, <c>RedactionPatterns</c>).
    /// Empty dictionary when the key does not exist.
    /// </summary>
    IReadOnlyDictionary<string, object> Read(PolicyHive hive, bool recommended);

    /// <summary>
    /// A two-level keyed list under the mandatory root:
    /// <c>…\Wrapp\&lt;listName&gt;\&lt;entryKey&gt;\values</c> — the shape the
    /// tenant/site/domain lists use so an admin can provision MULTIPLE
    /// entries (subkey name = the entry's Key). Empty when absent.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> ReadKeyedList(PolicyHive hive, string listName);
}
