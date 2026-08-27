# Wrapp — Security & Production-Readiness Audit

**Date:** 2026-07-09
**Scope:** `src/Wrapp.GUI` (C# WPF .NET 8), `modules/Wrapp.Packager` (PowerShell), the .NET→PowerShell bridge, MSAL/auth, and the Azure DevOps key vault.
**Baseline:** commit `2ec72d6` (post PR #1/#2/#3 merge), version `0.6.0.0245`, 602 tests green.
**Method:** seven parallel read-only review agents across six dimensions (secrets/crypto ×2 passes, MSAL/auth, PS bridge, data boundaries, dead-code/duplication, production readiness). Every HIGH/MEDIUM finding below was re-verified by hand against source before inclusion — each carries a ✅ *Verified* note with the exact line.

---

## 1. Executive summary

Wrapp is a **maturely engineered** codebase. The security architecture is deliberate: DPAPI-v2 envelopes with per-app entropy and self-healing v1 migration, `SecureString` end-to-end for client secrets with zero-freed BSTR windows, a `ref:settings` sentinel keeping secrets out of git-committed bundles, opaque-GUID MSAL refresh handles, DPAPI-encrypted token caches behind cross-process mutexes, atomic file writes, a redacting async logger, and global exception hooks. Broad classes of vulnerability were checked and found **absent**: no zip-slip, no XXE, no disabled TLS, no `BinaryFormatter`/`TypeNameHandling` deserialization gadgets, no `Invoke-Expression`/dynamic `Add-Type` in the PS module, no hardcoded credentials, no SSRF (hosts hard-pinned).

The audit nonetheless found **two HIGH** issues that undermine specific controls, plus a set of MEDIUM/LOW hardening items:

- **SEC-1 (HIGH):** the key-vault "TOFU" URL-trust check is **forgeable** — its hash lives in the same `settings.json` it's meant to protect and is an unkeyed `SHA256`. The control fails at its own documented purpose (defending against a malicious `settings.json` edit), and it guards the crown-jewel asset: every packaged app's AES content-decryption keys.
- **SEC-2 (HIGH):** a `cmd /c` string-concatenation sink in the packager module allows **command injection** through a config-derived source path.

Neither is a remote/unauthenticated internet vuln — Wrapp is a local admin desktop tool, so the realistic adversary is a **malicious or compromised shared config/bundle, a roaming/settings-sync channel, or a sibling process running as the same user**. Both HIGH findings are reachable within exactly that threat model.

**Production-readiness verdict:** stable and close to production-grade. One data-loss robustness bug (corrupt `settings.json` discards the backup) and one latent `SecureString` use-after-dispose crash should be fixed before a wide release; the rest are hardening and cleanup.

### Severity tally

| Severity | Security | Stability | Notes |
|---|---|---|---|
| HIGH | 2 | 0 | SEC-1 TOFU forgeable, SEC-2 cmd injection |
| MEDIUM | 5 | 3 | incl. unauthenticated decrypt, git arg-injection, over-broad Graph scope |
| LOW | 8 | 2 | defense-in-depth / advisory |
| INFO (by-design) | several | — | entropy constant, cache asymmetry, etc. |

---

## 2. Threat model (read this first — it calibrates every severity)

Wrapp runs as an **interactive IT-admin desktop app** on a trusted workstation. It is not a network service. The credible adversaries are:

1. **Malicious/compromised shared artifact** — a `Config.json` bundle or an exported settings file authored or tampered by someone other than the operator, then opened/imported. Bundles and configs are explicitly designed to be shared (the PR-mode vault feature exists for team workflows).
2. **Settings-sync / roaming-profile channel** — `settings.json` in `%LOCALAPPDATA%` can be written by profile roaming, backup-restore, or a second tool **without full code execution** as the user.
3. **Same-user sibling process** — opportunistic malware harvesting DPAPI blobs or the clipboard.

"HIGH" here means *reachable by (1) or (2) with meaningful impact* — not "remote pre-auth RCE." That is the correct bar for this class of tool.

---

## 3. Security findings (severity-ranked)

| ID | Sev | Title | Location |
|----|-----|-------|----------|
| SEC-1 | **HIGH** | TOFU key-vault URL trust is forgeable; reads skip it entirely | `EncryptionKeyStoreService` |
| SEC-2 | **HIGH** | Command injection via `cmd /c` + config-derived source path | `New-IntuneWin32Package.ps1` |
| SEC-3 | MED | Vault AES keys stored plaintext in git (permanent history); false "key will be lost" dialog | `EncryptionKeyStoreService`, `ToolsViewModel` |
| SEC-4 | MED | Git argument injection via string-concatenated process args | `GitService` |
| SEC-5 | MED | PS Error/Verbose streams reach UI/log without `Redact` | `PowerShellService` |
| SEC-6 | MED | Over-broad `Group.ReadWrite.All` Graph scope on a shared MS client | `MsalAuthService` |
| SEC-7 | MED | `.intunewin` AES-CBC decrypt is unauthenticated (HMAC never checked) | `IntuneWinService` |
| SEC-8 | LOW | DPAPI v1/plaintext read fallback enables secret *substitution* | `SecretProtection.Decrypt` |
| SEC-9 | LOW | WebView2 DevTools + context menus left at defaults | `MonacoHost` |
| SEC-10 | LOW | Help-link launcher opens any URI scheme (`file:`/`ms-*`) | `HelpMarkdownRenderer.Inlines` |
| SEC-11 | LOW | Full AES key copied to clipboard and shown in UI grid | `ToolsViewModel` |
| SEC-12 | LOW | DevOps token cache: plain DPAPI, no entropy/mutex (asymmetric w/ Intune cache) | `DevOpsAuthService` |
| SEC-13 | LOW | Fabricated 55-min token expiry → possible mid-run 401 | `PowerShellService` |
| SEC-14 | LOW | Single-quote escaping used inside a double-quoted PS string | `AppInventoryService` |
| SEC-15 | LOW | Pooled-runspace token clear may target the wrong runspace | `PowerShellService` |

### SEC-1 — TOFU key-vault URL trust is forgeable (HIGH)

**Where:** `ComputeKeyVaultUrlHash` [EncryptionKeyStoreService.cs:251](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L251); push guard [:306-315](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L306); hash stored at [AppSettings.cs:113](../../src/Wrapp.GUI/Models/AppSettings.cs#L113); read paths that skip the guard: [:118](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L118) (`LoadAllDevOpsKeysAsync`), fetch/exists at `FetchFromDevOpsAsync`/`CheckDevOpsExistsAsync`.

**Verified ✅:** the guard compares `_settings.KeyVaultRepoUrlHash` against `ComputeKeyVaultUrlHash(_settings.KeyVaultRepoUrl)`. `ComputeKeyVaultUrlHash` is a `public static` unkeyed `SHA256(url.trim().trimEnd('/').lower())`. Both operands originate from the same `settings.json`.

**Exploit:** a malicious config-sync or sibling writer sets **both** `KeyVaultRepoUrl = https://dev.azure.com/{attacker-org}/...` and `KeyVaultRepoUrlHash = <sha256 of that url>` (trivially recomputed). The guard passes with no prompt; the next packaging run pushes every app's AES decryption keys to an attacker-arranged DevOps repo the victim's token can write to. The in-code comment ([AppSettings.cs:108-111](../../src/Wrapp.GUI/Models/AppSettings.cs#L108)) claims the mechanism "prevents a settings.json edit … from silently redirecting every future key push" — it only stops an editor unaware of the hash field. Additionally the **read** paths never check the hash, so decrypt/brute-force will silently pull attacker-controlled key material from an unapproved repo.

**Severity note:** a second review pass argued MEDIUM (writing `settings.json` ≈ near-user-level access, so TOFU is "already moot"). Retained as **HIGH** because (a) `settings.json` is writable by non-code-exec vectors (roaming/sync/restore), and (b) the control explicitly advertises protection against exactly this and fails — a security control that doesn't do what it says, guarding the highest-value secret, is a HIGH.

**Fix:** bind the approval to something not forgeable from the file alone — store an **HMAC-SHA256(approved-url)** keyed by a random per-install secret sealed with `SecretProtection.ProtectBytes` (DPAPI, outside `settings.json`), or DPAPI-protect the whole approval record. Apply the same check to the read paths (`FetchFromDevOpsAsync`, `CheckDevOpsExistsAsync`, `LoadAllDevOpsKeysAsync`).

### SEC-2 — Command injection via `cmd /c` + config-derived path (HIGH)

**Where:** [New-IntuneWin32Package.ps1:88-95](../../modules/Wrapp.Packager/Public/New-IntuneWin32Package.ps1#L88).

**Verified ✅:** `$robocopyArgs` double-quotes `$SourcePath` (line 88), then `$robocopyCmd = "robocopy $($robocopyArgs -join ' ')"` (93) is handed to `cmd /c $robocopyCmd` (95). `cmd`'s parser is independent of PowerShell's quoting.

**Exploit:** a config carrying `SourcePath = C:\pkg" & calc.exe & "` breaks out of the double quotes; `cmd` treats `&` as a command separator and runs arbitrary commands. Reachable when the config has excluded dirs (`$HasExcluded`) and a crafted source path — both config-derived and thus attacker-controllable via a shared bundle/config. `$SourcePath` is not passed through `Test-SafePath` on this branch.

**Fix:** drop `cmd /c` and string concat entirely — invoke directly with array splat: `& robocopy @robocopyArgs`. Robocopy's exit code is still available via `$LASTEXITCODE`.

### SEC-3 — Vault AES keys are plaintext in git, forever (MEDIUM)

**Where:** serialized cleartext + committed [EncryptionKeyStoreService.cs:332](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L332) and the PR path; overwrite dialog [ToolsViewModel.cs:775](../../src/Wrapp.GUI/ViewModels/ToolsViewModel.cs#L775).

`EncryptionKeyInfo.EncryptionKey`/`MacKey`/`IV` are base64 plaintext; the repo ACL is the stated control surface (documented design). Aggravators: (a) git history means "Overwrite" collision resolution **never destroys** the old key — the overwrite dialog's "existing key will be lost" text is **false** in a git-backed vault; (b) PR mode leaves keys on feature branches and in PR diffs even if the PR is declined, widening exposure to anyone with PR-read.

**Fix:** envelope-encrypt the key JSON before commit (per-tenant KMS/`age` recipient) so repo-read alone is insufficient; correct the dialog wording; document that vault-history exposure is **permanent** and rotation must assume the old key is compromised.

### SEC-4 — Git argument injection via string args (MEDIUM)

**Where:** [GitService.cs:461-463](../../src/Wrapp.GUI/Services/GitService.cs#L461).

**Verified ✅:** `var fullArgs = $"-c safe.directory=\"{safePath}\" {arguments}";` then `new ProcessStartInfo(gitExe, fullArgs)` — a single interpolated string, which Windows re-parses. `arguments`/paths can flow from imported-bundle files (full-clone extracts attacker-named content). `UseShellExecute=false` blocks shell metacharacters but **not** git-option injection (e.g. a crafted `--upload-pack`/`-c` payload).

**Fix:** use `ProcessStartInfo.ArgumentList` (each arg added as a separate element — no re-parsing).

### SEC-5 — PS Error/Verbose streams bypass redaction (MEDIUM)

**Where:** [PowerShellService.cs:420-427](../../src/Wrapp.GUI/Services/PowerShellService.cs#L420).

**Verified ✅:** the stream handlers do `log.Report($"[ERROR] {…Exception?.Message…}")` straight to the `IProgress` sink; `AppLogger.Redact` is applied by `AppLogger.Info/Warn` but **not** on the `log.Report`→`UiProgress` path. A failed `Invoke-RestMethod` can surface an exception body echoing an `Authorization: Bearer …` header into the run log / app.log.

**Fix:** route stream text through `AppLogger.Redact` before `log.Report`. (Best solved by unifying all user-facing/log output through one redacting sink — see Reform §7.)

### SEC-6 — Over-broad Graph scope (MEDIUM)

**Where:** [MsalAuthService.cs:24-38](../../src/Wrapp.GUI/Services/MsalAuthService.cs#L24).

Requests `DeviceManagementApps.ReadWrite.All`, `DeviceManagementConfiguration.ReadWrite.All`, `DeviceManagementRBAC.ReadWrite.All`, **`Group.ReadWrite.All`** under the shared Microsoft Graph PowerShell public client. `Group.ReadWrite.All` (full directory group read/write) far exceeds app-packaging needs and can't be scoped down under a shared first-party client.

**Fix:** drop `Group.ReadWrite.All` to the narrowest group scope actually exercised; recommend a **customer-owned Entra app registration** so orgs can apply least-privilege consent + conditional access.

### SEC-7 — Unauthenticated AES-CBC decrypt (MEDIUM)

**Where:** [IntuneWinService.cs:77-104](../../src/Wrapp.GUI/Services/IntuneWinService.cs#L77).

**Verified ✅:** `DecryptAsync` seeks past the 48-byte header (32-byte HMAC + 16-byte IV) at line 91 and CBC-decrypts, **never** verifying `Mac` with `MacKey` (both present in `EncryptionKeyInfo`), and never checks `FileDigest`. Inputs can be arbitrary (dropped `.intunewin`, downloaded Azure blobs); plaintext is written to disk and parsed (`ExtractAppIdentityAsync`). No remote padding oracle (local decrypt) → integrity-only, hence MEDIUM.

**Fix:** verify `HMAC-SHA256(MacKey, IV‖ciphertext)` against `Mac` when `MacKey` is available (embedded + vault paths have it); warn when absent.

### SEC-8..SEC-15 (LOW)

- **SEC-8** [AppSettings.cs `Decrypt` fallback](../../src/Wrapp.GUI/Models/AppSettings.cs#L404): non-`dpapi:` input is returned verbatim as "legacy plaintext"; combined with a `settings.json` write this permits secret *substitution* (redirect auth to an attacker app registration). *Fix:* once migrated, refuse non-`dpapi:v2:` values for secret fields.
- **SEC-9** [MonacoHost.cs:23](../../src/Wrapp.GUI/Services/MonacoHost.cs#L23): `AreDevToolsEnabled`/`AreDefaultContextMenusEnabled` unset (default on). Strongly mitigated (local host-mapped content, no `AddHostObjectToScript`, JSON-escaped injection). *Fix:* disable both in production.
- **SEC-10** [HelpMarkdownRenderer.Inlines.cs:240](../../src/Wrapp.GUI/Helpers/HelpMarkdownRenderer.Inlines.cs#L240): `ShellExecute` link launch with no scheme allowlist. Content is static help today. *Fix:* allowlist `http`/`https`/`mailto`.
- **SEC-11** [ToolsViewModel.cs:147](../../src/Wrapp.GUI/ViewModels/ToolsViewModel.cs#L147) (clipboard) + `KeyPreview` carrying the **full** base64 key into the grid + auto-populated `ManualKey`/`ManualIV`. Win+V history / cloud-clipboard / screenshare exposure. *Fix:* truncate `KeyPreview`, clipboard-clear timer, don't auto-fill Manual fields.
- **SEC-12** [DevOpsAuthService.cs:139](../../src/Wrapp.GUI/Services/DevOpsAuthService.cs#L139): `MsalCacheHelper` DPAPI without the v2 entropy/mutex the Intune cache uses. Documented asymmetry; low. *Fix:* accept + note in threat-model docs.
- **SEC-13** [PowerShellService.cs:702](../../src/Wrapp.GUI/Services/PowerShellService.cs#L702): `'ExpiresOn' = (Get-Date).AddMinutes(55)` — fabricated, not the real `ExpiresOnUtc`; PS-side refresh may skip → mid-run 401. Reliability. *Fix:* pass `token.ExpiresOnUtc`.
- **SEC-14** [AppInventoryService.cs:636](../../src/Wrapp.GUI/Services/AppInventoryService.cs#L636): single-quote escaping applied to a value that lands in a **double-quoted** PS string (wrong metacharacters). Input is a Graph GUID (low). *Fix:* single-quote literal or `AddParameter`.
- **SEC-15** [PowerShellService.cs:511-514](../../src/Wrapp.GUI/Services/PowerShellService.cs#L511): the C# "belt" token-clear binds a fresh `PowerShell` to the 3-runspace pool and may clear a **different** runspace than the one that holds `$Global:AccessToken`; the in-script `finally` is the real clear. *Fix:* pin inject+run+clear to one dedicated `Runspace`, or `MaxRunspaces(1)`.

### Verified-SAFE (checked, no action — documents breadth)

Zip-slip (`ZipFile.ExtractToDirectory` validates; inner extraction uses `Path.GetFileName`) · XXE (`XDocument` defaults prohibit DTDs) · TLS (no custom/disabled validation anywhere) · deserialization (only `System.Text.Json`, no `TypeNameHandling`; zero `BinaryFormatter`/`XmlSerializer`) · SSRF (Graph `graph.microsoft.com/beta` + DevOps `dev.azure.com` hard-pinned; Bearer withheld from Azure blob GETs) · no `Invoke-Expression`/`iex`/dynamic `Add-Type`/remote-content exec in the module · MSAL authority pinned (`AzurePublic`, not config-controllable) · refresh handles unforgeable (122-bit GUID → in-proc registry, cleared in `finally`) · cache mutex correct (`AbandonedMutexException` handled, atomic `File.Replace`) · WAM parenting to own main window (no spoof) · `WithPlaintext` sanitizes secret echo from exceptions + zero-frees the BSTR · token injected via `AddParameter` (never string-spliced; not in env/argv/PSReadLine) · `FeatureGateService` fails closed · no hardcoded credentials · fail-closed `SecretEncryptionException` (no plaintext downgrade).

---

## 4. Stability / production-readiness findings

| ID | Sev | Title | Location |
|----|-----|-------|----------|
| STA-1 | MED | Corrupt `settings.json` loses all data **and** skips `.bak` recovery | `SettingsService.Load` |
| STA-2 | MED | `SecureString` use-after-dispose via shared clone reference (crash + secret-lifecycle) | `PreferencesSync` / `SettingsService` |
| STA-3 | MED | `async void` domain-event handlers not `SafeFireAndForget`-wrapped (terminating) | `GitHistoryViewModel`, `RunViewModel` |
| STA-4 | LOW | Empty `catch` swallows malformed DevOps API responses (not just empty-repo) | `EncryptionKeyStoreService` |
| STA-5 | LOW | No HTTP seam → vault push/PR path untestable | `EncryptionKeyStoreService` |

**STA-1 ✅** [SettingsService.cs:163-184](../../src/Wrapp.GUI/Services/SettingsService.cs#L163): primary read+`Deserialize` and the `.bak` fallback are inside **one** `try`. A *corrupt* (not missing) primary throws at `Deserialize`, jumps to the outer `catch`→`new AppSettings()`, and the `.bak` block is never reached. Result: all saved tenants/sites/theme silently lost with only a `Warn`. *Fix:* on primary-parse failure, explicitly attempt `.bak` before returning defaults, and surface a startup warning to the user.

**STA-2 ✅** (cross-cut with secrets) [PreferencesSync.cs:76](../../src/Wrapp.GUI/Services/PreferencesSync.cs#L76) shares `ClientSecret` by reference; [SettingsService.cs:136-137](../../src/Wrapp.GUI/Services/SettingsService.cs#L136) disposes+nulls only the *source*. A clone that shared the instance later hits `ObjectDisposedException` on `ClientSecret.Length` (e.g. bundle serialize `is { Length: > 0 }`). Reachability depends on save-then-serialize ordering — latent but real. *Fix:* deep-copy in `CloneTenant` via `SecretProtection.ResolveTenantSecret(src.ClientSecretCipher, src.ClientSecret)`, or copy only the cipher; establish a one-owner rule for live `SecureString`s.

**STA-3** [GitHistoryViewModel.cs:40](../../src/Wrapp.GUI/ViewModels/GitHistoryViewModel.cs#L40), [RunViewModel.cs:70](../../src/Wrapp.GUI/ViewModels/RunViewModel.cs#L70): `async void` handlers on **non-UI** domain events. A fault on a thread-pool continuation escapes to `AppDomain.UnhandledException` (terminating) rather than the `DispatcherUnhandledException` net. *Fix:* wrap in the existing `SafeFireAndForget.Run`. (UI-event `async void` in `*.xaml.cs` are fine — covered by the Dispatcher hook.)

**STA-4** [EncryptionKeyStoreService.cs:357](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L357) & the PR-path equivalent: the "empty repo → all-zeros objectId" `catch {}` also swallows a genuinely malformed API response (schema change, auth error page), then pushes with a bogus base commit. *Fix:* narrow to `catch (JsonException/KeyNotFoundException)` and `Warn` before defaulting.

**STA-5** [EncryptionKeyStoreService.cs:21](../../src/Wrapp.GUI/Services/EncryptionKeyStoreService.cs#L21): `private static readonly HttpClient Http = new();` referenced directly — correct for sockets but leaves the push/PR/branch logic unmockable (the known seam gap; PR-routing tests could only assert via message prefixes). *Fix:* inject `HttpMessageHandler` (or `IKeyVaultTransport`) with the static as default. Naturally solved by the `DevOpsGitClient` extraction (QLT-1).

**Confirmed-good (production-readiness):** global exception hooks (Dispatcher/AppDomain/TaskScheduler) · `SafeFireAndForget` correctly the one legitimate `async void` · static `HttpClient` throughout (no socket exhaustion) · matched `+=`/`-=` on `App.ThemeChanged`, `CancellationTokenSource` disposal · atomic writes · single-instance + cache mutex correctness · `Nullable` enabled project-wide · **no** `TODO`/`HACK`/`FIXME`/`NotImplementedException` in app code · only bounded blocking calls (PS pipeline thread, 2s log-drain).

---

## 5. Dead code & duplication (remaining after prior CLEANUP cycles)

The earlier `CLEANUP_BACKLOG`/`CLEANUP_GOALS` cycles were thorough; the dead-code sweep is essentially complete. What remains:

| ID | Kind | Item | Effort |
|----|------|------|--------|
| QLT-1 | Duplication | **D-8**: DevOps REST layer — `Bearer` request build ×10, `ParseRepoUrl`+token+`apiBase` preamble ×5, duplicated `refs`/`items` blocks → extract `DevOpsGitClient` (~150 LOC) | Medium |
| QLT-2 | Dead code | `DevOpsAuthService` status API (`IsAuthenticated`, `Username`, `TokenExpiresUtc`, `TokenAcquired`) — grep-verified zero subscribers | Trivial |
| QLT-3 | Duplication | `AtomicFile` write-body triplicated (`WriteAllText/Bytes/Async`) → private `Commit(path, writeTemp)` | Trivial |
| QLT-4 | Duplication | `MsiPropertyService` open→view→execute P/Invoke boilerplate ×3 → `RunScalarQuery` | Small |
| QLT-5 | Duplication | `TemplateService.Str` duplicates `JsonObjectExtensions.Str` → delegate | Trivial |
| QLT-6 | — | Open backlog `G3.x`/`G4.x` (token replacement, `FileSignature`, `Sanitize`, `SecretProtection` relocation, doc fixes) — already documented | Various |

**QLT-1 is the keystone:** the `DevOpsGitClient` extraction is not just DRY — it creates the *single* place to (a) add the SEC-1 read-path trust check, (b) apply SEC-3 envelope encryption, and (c) inject the STA-5 HTTP seam. Do it early and three other fixes get cheaper.

---

## 6. Remediation plan (phased)

### P0 — Security-critical (do first; ~1–2 sittings)
1. **SEC-2** robocopy `cmd /c` → `& robocopy @robocopyArgs`. *Trivial, isolated.*
2. **SEC-4** `GitService` → `ProcessStartInfo.ArgumentList`. *Small.*
3. **SEC-1** machine-bound HMAC/DPAPI approval anchor + apply trust check to read paths. *Medium (pairs with QLT-1).*
4. **SEC-7** HMAC verification in `DecryptAsync`. *Small.*
5. **STA-2** deep-copy `SecureString` in `CloneTenant`. *Trivial — prevents a crash.*

### P1 — Security hardening + stability (~1–2 sittings)
6. **SEC-5** route PS streams through `Redact` (or a unified redacting sink).
7. **STA-1** `.bak` fallback on corrupt primary + user-visible warning.
8. **SEC-6** narrow Graph scopes; open a decision on a customer app registration.
9. **SEC-3** envelope-encrypt vault keys + fix the false overwrite-dialog text + document history permanence.
10. **SEC-8** reject non-v2 secret ciphertext post-migration.
11. **STA-3** wrap the two `async void` domain handlers in `SafeFireAndForget`.

### P2 — Defense-in-depth + quality (as capacity allows)
12. **QLT-1** extract `DevOpsGitClient` (also delivers **STA-5** seam).
13. **SEC-9/10/11/13/14/15** the LOW hardening set.
14. **QLT-2..5** dead-code + trivial dup consolidations.
15. **STA-4** narrow the refs-parse catch.

Each item is independently shippable behind the existing test suite; suggest one PR per phase, version-bumped and CHANGELOG'd per the established cadence.

---

## 7. Reform / best-practice recommendations

1. **A machine-bound integrity key for security-relevant settings.** SEC-1 and SEC-8 are the same root cause: `settings.json` is treated as trusted but is attacker-writable. Introduce one DPAPI-sealed per-install secret and HMAC the security-sensitive fields (approved vault URL, and ideally the whole settings blob). This converts "anyone who edits the file" into "requires code execution as the user."
2. **Never build process/PS command lines by string concatenation.** SEC-2 and SEC-4 are both this anti-pattern. Standardize on `ArgumentList` for `Process` and `AddCommand`/`AddParameter` (or `@splat`) for PowerShell. **Add a lint rule** (the repo already has `SourceLintTests`) forbidding `cmd /c` with interpolation and `new ProcessStartInfo(exe, <interpolated string>)`.
3. **One redacting output sink.** SEC-5 exists because two logging paths (`AppLogger` vs `UiProgress`/`log.Report`) have different redaction. Funnel all user-surfaced and file-logged text through a single sink that always calls `Redact`, and extend `Redact` with an `encryption[_-]?key|mac[_-]?key|initialization[_-]?vector` value regex (INFO gap noted by two agents).
4. **Authenticated encryption everywhere.** Add HMAC verification to the decrypt path (SEC-7); treat "we have the MacKey but don't check it" as a bug class.
5. **Centralize the DevOps transport (QLT-1) and give it a test seam.** One `DevOpsGitClient` with an injectable `HttpMessageHandler` removes ~150 lines, closes the STA-5 test gap, and is the natural home for the SEC-1 trust check and SEC-3 envelope encryption.
6. **Least-privilege identity.** Move off the shared Graph PowerShell client to a customer-owned Entra app registration with only the scopes Wrapp exercises (SEC-6) — this is the single biggest real-world blast-radius reduction.
7. **Clear `SecureString` ownership rule.** STA-2 came from ambiguous ownership across clone/save. Rule: clones copy the **cipher**, never the live `SecureString`; exactly one owner disposes.
8. **CI hardening.** Turn on `TreatWarningsAsErrors` for `Release`, enable the .NET security/quality analyzers (CA rules), and run the test suite + lint on every PR. Consider a secret-scanning pre-commit hook given the vault-key material in play.
9. **Threat-model doc.** Fold §2 of this report into `docs/architecture/` and note the accepted risks (entropy constant, DevOps-cache asymmetry, vault-history permanence) so future reviewers don't re-litigate them.

---

## 8. Appendix — coverage

Seven read-only agents: secrets/crypto (two passes — the second found STA-2/SEC-7/SEC-11 the first missed), MSAL/auth, PS bridge, data boundaries, dead-code/duplication, production readiness. All HIGH/MEDIUM findings hand-verified against source at the cited lines. No code was modified during this audit.
