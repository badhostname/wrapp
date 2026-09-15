using System.IO;
using System.Linq;
using System.Text.Json;
using Wrapp.Models;

namespace Wrapp.Services;

/// <summary>
/// Machine-bound values of SENSITIVE custom placeholders.
/// Lives in <c>placeholders.secure.json</c> beside settings.json as
/// name → "dpapi:v2:..." envelopes (<see cref="SecretProtection"/>), so the
/// portable preferences file carries the placeholder ROW (name, sensitive
/// flag, comment) but never the value: exports stay shareable, values stay
/// on this machine - the tenant-ClientSecret rule, in a sidecar.
/// Writes are serialized cross-process and atomic like every other store.
/// </summary>
public static class PlaceholderSecureStore
{
    private const string LockName = "Placeholders";

    /// <summary>Test seam: overrides the sidecar path (null = production location).</summary>
    internal static string? PathOverride;

    private static string StorePath => PathOverride
        ?? Path.Combine(PlatformConfig.WrappRoot, "placeholders.secure.json");

    /// <summary>Names present in the store.</summary>
    public static IReadOnlyCollection<string> Names()
        => LoadEnvelopes().Keys;

    /// <summary>Decrypted value for <paramref name="name"/>, or null when absent/undecryptable.</summary>
    public static string? GetValue(string name)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!LoadEnvelopes().TryGetValue(name, out var envelope)) return null;
            // This file has NO legacy plaintext format - SetValue has only
            // ever written DPAPI v2 envelopes. DecryptAuthentic refuses anything
            // that isn't an authentic envelope, so a value planted into the file
            // as a bare string is rejected instead of silently accepted.
            return SecretProtection.DecryptAuthentic(envelope) is { Length: > 0 } plain ? plain : null;
        }
        finally
        {
            // DPAPI goes through an lsass RPC; a slow one on the UI thread is
            // stall material - make it visible when it happens.
            if (sw.ElapsedMilliseconds > 50)
                AppLogger.Warn($"Placeholders: secure-store read for '{name}' took {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// Encrypts and stores a value (empty/null removes the entry). Throws
    /// <see cref="SecretEncryptionException"/> when DPAPI is unavailable -
    /// callers surface it and refuse to persist, never downgrade to plaintext.
    /// </summary>
    public static void SetValue(string name, string? plaintext)
    {
        var envelope = string.IsNullOrEmpty(plaintext) ? null : SecretProtection.Encrypt(plaintext);
        Mutate(map =>
        {
            if (envelope is null) map.Remove(name);
            else map[name] = envelope;
        });
    }

    /// <summary>Removes an entry (e.g. placeholder deleted or made non-sensitive).</summary>
    public static void Remove(string name) => Mutate(map => map.Remove(name));

    /// <summary>
    /// Drops entries whose names are no longer sensitive placeholders - called
    /// after preference saves so renamed/deleted placeholders leave no orphan
    /// ciphertext behind.
    /// </summary>
    public static void PruneExcept(IEnumerable<string> keepNames)
    {
        var keep = new HashSet<string>(keepNames, StringComparer.OrdinalIgnoreCase);
        Mutate(map =>
        {
            foreach (var stale in map.Keys.Where(k => !keep.Contains(k)).ToList())
                map.Remove(stale);
        });
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Reads the envelope map. Transient <see cref="IOException"/>s (a
    /// scanner/indexer briefly holding the freshly-written file) are retried
    /// - without this, a blipped read inside <see cref="Mutate"/> produced an
    /// EMPTY map that the subsequent write persisted, silently wiping every
    /// other secure value. <paramref name="strict"/> (the Mutate path)
    /// rethrows a persistent IO failure so the mutation aborts instead of
    /// wiping; readers stay soft (missing value, logged). A CORRUPT file
    /// (bad JSON) still resets to empty in both modes - secrets are
    /// re-enterable, a permanently failing store is not.
    /// </summary>
    private static Dictionary<string, string> LoadEnvelopes(bool strict = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(StorePath)) return new(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(StorePath);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                           is { } map
                    ? new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException) when (attempt < 3)
            {
                System.Threading.Thread.Sleep(50);
            }
            catch (IOException ex) when (strict)
            {
                AppLogger.Warn($"Placeholders: secure store unreadable after {attempt} attempts -- aborting mutation to protect existing values: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                // A corrupt store still resets to empty (secrets are
                // re-enterable; a permanently failing store is not) - but the
                // ciphertext is preserved first, so if DPAPI still works the
                // values are recoverable by hand instead of silently gone.
                AppLogger.Warn($"Placeholders: could not read secure store: {ex.Message}");
                try
                {
                    var backup = StorePath + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                    File.Copy(StorePath, backup, overwrite: false);
                    AppLogger.Warn($"Placeholders: corrupt store preserved as {Path.GetFileName(backup)}");
                }
                catch { /* backup is best-effort; never block the reset path */ }
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void Mutate(Action<Dictionary<string, string>> change)
    {
        CrossProcessLock.Run(LockName, () =>
        {
            Dictionary<string, string> map;
            try { map = LoadEnvelopes(strict: true); }
            catch (IOException) { return; }   // logged above; better a skipped write than a wiped store
            change(map);
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            AtomicFile.WriteAllText(StorePath,
                JsonSerializer.Serialize(map, JsonDefaults.Pretty));
        });
    }
}
