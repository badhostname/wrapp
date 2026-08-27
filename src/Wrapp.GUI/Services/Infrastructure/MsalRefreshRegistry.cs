using System.Collections.Concurrent;
using Microsoft.Identity.Client;

namespace Wrapp.Services;

/// <summary>
/// Phase 12 hardening (S-3). Holds live <see cref="IPublicClientApplication"/>
/// + <see cref="IAccount"/> + scope references keyed by an opaque GUID handle,
/// so the running PowerShell scripts never see the MSAL objects directly.
///
/// Prior behaviour: <c>$Global:WrappMsalApp</c> exposed the live IPCA to every
/// runspace, letting any in-process PS script call
/// <c>AcquireTokenSilent(...)</c> with arbitrary scopes. We control all the PS
/// scripts in the runspace, but defence-in-depth says: the GUI&#x2019;s MSAL client
/// should not be reachable through a global variable.
///
/// New behaviour: <see cref="Register"/> stores the IPCA tuple, returns a GUID
/// handle, and the bridge injects only the handle. PS scripts call the
/// <c>Invoke-WrappTokenRefresh</c> cmdlet with that handle; the cmdlet looks
/// up the tuple here and performs the refresh in C#. The handle alone is
/// useless without registry access.
///
/// Lifecycle: handles must be unregistered after the PS run completes.
/// <see cref="PowerShellService"/> calls <see cref="Unregister"/> in a
/// <c>finally</c> so a thrown PS pipeline doesn&#x2019;t leak references.
/// </summary>
public static class MsalRefreshRegistry
{
    private static readonly ConcurrentDictionary<Guid, RefreshContext> _entries = new();

    /// <summary>
    /// Records a (IPCA, account, scopes) tuple and returns an opaque handle.
    /// </summary>
    public static Guid Register(IPublicClientApplication app, IAccount? account, string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(scopes);
        var handle = Guid.NewGuid();
        _entries[handle] = new RefreshContext(app, account, scopes);
        return handle;
    }

    /// <summary>
    /// Removes the handle&#x2019;s entry. Idempotent &#x2014; safe to call even if the
    /// handle was already removed or never registered.
    /// </summary>
    public static void Unregister(Guid handle)
        => _entries.TryRemove(handle, out _);

    /// <summary>
    /// Looks up the tuple registered for <paramref name="handle"/>. Returns
    /// <c>false</c> when the handle is unknown (caller treats this as
    /// &#x201C;refresh capability not available&#x201D; and falls back to the next tier).
    /// </summary>
    internal static bool TryGet(Guid handle, out RefreshContext context)
        => _entries.TryGetValue(handle, out context!);

    internal readonly record struct RefreshContext(
        IPublicClientApplication App,
        IAccount? Account,
        string[] Scopes);
}
