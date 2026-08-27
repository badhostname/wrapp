using System.Runtime.CompilerServices;
using Wrapp.Services.Policy;

namespace Wrapp.Tests;

/// <summary>
/// Test hermeticity: the suite must NEVER read the host machine's real
/// registry policy — a developer (or CI runner) with Wrapp policy applied
/// via Apply-WrappPolicy.ps1/GPO would otherwise leak mandates into every
/// test that touches settings (discovered when applying a real local policy
/// made SettingsPortabilityTests fail: the machine-mandated feed URL was
/// re-asserted over the test's imported value, and the machine-mandate TOFU
/// bypass made the feed read as trusted). An empty in-memory store is the
/// assembly-wide default; PolicyServiceTests swap in their own and restore
/// an empty one — never the registry store — on dispose.
/// </summary>
internal static class PolicyTestIsolation
{
    [ModuleInitializer]
    internal static void Init()
        => PolicyService.OverrideStore(new InMemoryPolicyStore());
}
