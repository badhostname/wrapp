# Account/Profile UI Comparison: IntuneManagement vs Wrapp GUI

## Executive Summary

IntuneManagement's profile/account UI is **100% token-cache-driven**. It has zero dependency
on any configuration file for displaying cached accounts. Tenant display names are saved to
local settings during each successful login and retrieved from those settings when showing
cached accounts later.

Wrapp GUI's current implementation incorrectly resolves tenant display names from
Config.json's IntuneTenants collection and added a config-driven "Tenant Switcher" that has
no equivalent purpose in IntuneManagement. The config-based tenant switcher should be removed
and replaced with a cache-driven approach matching IntuneManagement's pattern.

---

## Data Source Comparison

| Data Point | IntuneManagement | Wrapp GUI (current) |
|---|---|---|
| Cached account list | `$global:MSALApp.GetAccountsAsync()` (MSAL cache) | `_publicApp.GetAccountsAsync()` (MSAL cache) |
| UPN per account | `$account.UserName` (from IAccount) | `account.Username` (from IAccount) |
| Tenant name per account | `Get-Setting {TenantId}_Name` (local settings file) | Config.json IntuneTenants lookup by Key (WRONG) |
| Tenant name source | Saved from Graph `/organization` during previous login | Never saved; only available if tenant is in config |
| Tenant name fallback | Raw tenant ID GUID | Raw tenant ID GUID |
| Config dependency | NONE | IntuneTenants collection (incorrect coupling) |

---

## How IntuneManagement Saves Tenant Names

During every successful authentication, IntuneManagement calls `Get-MSALUserInfo`
(MSALAuthentication.psm1 line 287):

```powershell
# After fetching /organization from Graph API:
Save-Setting $global:Organization.Id "_Name" $global:Organization.displayName
```

This saves `{TenantId}_Name = "Contoso Corp"` to the local settings store
(`%LOCALAPPDATA%\CloudAPIPowerShellManagement\settings.json` or registry).

Later, when building the cached accounts list, `Add-CachedUser` retrieves it
(MSALAuthentication.psm1 line 2143):

```powershell
$tenantName = Get-Setting $account.HomeAccountId.TenantId "_Name" $account.HomeAccountId.TenantId
```

This is a simple key-value lookup: `{TenantId}_Name` -> display name, with the tenant ID
GUID as fallback if no name was ever saved.

**Wrapp GUI does NOT do this.** It resolves `TenantDisplayName` from Graph API for the
currently signed-in user (in `ApplyTokenResultAsync`) but never persists it. For cached
accounts, it looks up Config.json instead, which fails for unconfigured tenants.

---

## Not-Signed-In State Comparison

### IntuneManagement (LoginPanel)
- If zero cached accounts: immediately shows interactive login dialog
- If cached accounts exist: shows LoginPanel popup with:
  - One button per cached account: [LoggedOnUser icon] [UPN + tenant name] [Forget]
  - "Sign in with a different account" button at bottom
  - Clicking an account: `Connect-MSALUser -Account $account` (silent, falls back interactive)
  - **No config dependency whatsoever**

### Wrapp GUI (current)
- Shows "Not signed in" header + description + "Sign in" button
- Below: cached accounts from MSAL cache (same concept)
  - Each shows: [Initials circle] [UPN + tenant display] [Forget]
  - Tenant display resolved from Config.json (WRONG)
  - Clicking: `SwitchAccountCommand` -> `AcquireTokenForAccountAsync` (silent, falls back)
- Below that: **Config-based tenant switcher** (WRONG - not cache-driven)

---

## Signed-In State Comparison

### IntuneManagement (ProfileInfo popup)
Row 0: Organization name + "Sign out" button
Row 1: Display name (large 24pt)
Row 2: Logon name (UPN)
Row 3: App name + App ID
Row 4: "Request Consent" button
Row 5: Token info links (MSAL Token, Access Token, Id Token, Refresh)
Row 8: **grdCachedAccounts** - Other cached users (excluding current), from MSAL cache
Row 9: **grdLoginAccount** - "Sign in with a different account"
Row 10: **grdTenantAccounts** - Accessible tenants (from Azure Management API, NOT config)

### Wrapp GUI (current)
- User card: Initials, DisplayName, UPN, TenantDisplayName, AuthFlow
- App info: AppDisplayName, AppId, TokenExpiry
- Token buttons: Access Token, Id Token, Refresh
- Tenant config status (green checkmark / amber warning)
- Sign out button
- Cached accounts (excluding current) with forget buttons
- "Sign in with a different account"
- **Config-based tenant switcher** (WRONG)
- Status text

---

## The Three Grids in IntuneManagement (and what they mean)

### grdCachedAccounts (Row 8)
- **Source**: `$global:MSALAccounts` (MSAL token cache)
- **Shows**: Other cached USER ACCOUNTS (different UPNs)
- **Per entry**: Icon + UPN + saved tenant name + Forget button
- **Click action**: Switch to that USER (silent auth with that IAccount)
- **Equivalent in Wrapp**: CachedAccounts ItemsControl (this is correct)

### grdLoginAccount (Row 9)
- **Shows**: Single "Sign in with a different account" button
- **Click action**: `Connect-MSALUser -Interactive -ShowMenu` (account picker)
- **Equivalent in Wrapp**: SignInDifferentAccountCommand button (this is correct)

### grdTenantAccounts (Row 10)
- **Source**: `$script:AccessableTenants` (Azure Management REST API)
- **Shows**: Tenants the current user has ACCESS to (multi-tenant scenario)
- **Per entry**: Tenant DisplayName + defaultDomain + tenantId
- **Click action**: Re-auth same user to a different tenant (`$global:MSALTenantId = tenantId`)
- **Visible**: Only when signed in AND 2+ accessible tenants AND GetTenantList setting true
- **NOT equivalent to our config tenant switcher**: This discovers tenants via API, not config

---

## Root Cause of Current Issues

### Issue 1: Tenant names show GUIDs instead of friendly names
**Cause**: `RefreshCachedAccountsAsync` resolves tenant names from Config.json's IntuneTenants.
If the cached account's tenant isn't in the config, it falls back to the raw GUID.

**IntuneManagement fix**: Tenant names are saved to local settings during each login and
retrieved from there. The settings file acts as a persistent cache of tenant display names
that grows over time as the user authenticates to different tenants.

### Issue 2: Config-based tenant switcher is conceptually wrong
**Cause**: The tenant switcher shows IntuneTenantEntry objects from Config.json. These are
packaging configuration entries (with ClientID, AuthFlow, ClientSecret, etc.), not
previously-authenticated tenants. A user who has never authenticated to a configured tenant
sees it in the switcher, while a user who authenticated to an unconfigured tenant doesn't.

**IntuneManagement's grdTenantAccounts** is entirely different: it discovers tenants via
the Azure Management API (`/tenants?api-version=2020-01-01`) that the CURRENT user has
access to. It requires a separate token with `management.azure.com/user_impersonation`
scope. This is an optional, signed-in-only feature controlled by a setting.

### Issue 3: No persistent tenant name storage
**Cause**: Wrapp GUI resolves the signed-in tenant's display name from Graph API
(`ResolveOrganizationNameAsync`) but never persists it. The resolved name is only stored
in `TenantDisplayName` (in-memory property). When showing cached accounts, there's no way
to retrieve previously-resolved tenant names.

---

## Recommended Changes

### 1. Add persistent tenant name storage
Save `{tenantId} -> organizationDisplayName` to settings.json during each successful auth.
Read from this store when displaying cached accounts. Mirrors IntuneManagement's
`Save-Setting {TenantId}_Name` / `Get-Setting {TenantId}_Name` pattern.

### 2. Remove config-based tenant switcher
Remove `ConfiguredTenants`, `SwitchTenantCommand`, `RefreshConfiguredTenants()`, and the
tenant switcher XAML sections. These are conceptually wrong -- cached accounts already
handle the "quick re-auth" scenario.

### 3. Fix tenant display name resolution for cached accounts
Change `RefreshCachedAccountsAsync` to resolve tenant names from the persistent store
(step 1) instead of Config.json. Fall back to tenant ID GUID if no saved name exists.

### 4. Remove StringEqualsToVisibilityConverter
No longer needed without the tenant switcher highlighting.

---

## File References

### IntuneManagement
| File | Key Lines | Purpose |
|---|---|---|
| Extensions/MSALAuthentication.psm1 | 287 | Save tenant name: `Save-Setting $org.Id "_Name" $org.displayName` |
| Extensions/MSALAuthentication.psm1 | 1707-2113 | Get-MSALProfileEllipse (profile popup builder) |
| Extensions/MSALAuthentication.psm1 | 2115-2199 | Add-CachedUser (cached account entry builder) |
| Extensions/MSALAuthentication.psm1 | 2143 | Resolve tenant name: `Get-Setting {TenantId}_Name` |
| Extensions/MSALAuthentication.psm1 | 976-1409 | Connect-MSALUser (auth flow) |
| Extensions/MSALAuthentication.psm1 | 1329-1393 | Accessible tenants discovery (Azure Mgmt API) |
| Extensions/MSALAuthentication.psm1 | 1652-1705 | Disconnect-MSALUser (forget account) |
| Xaml/ProfileInfo.Xaml | 54-73 | grdCachedAccounts, grdLoginAccount, grdTenantAccounts |
| Xaml/LoginPanel.xaml | 1-8 | Not-signed-in cached accounts grid |
| Extensions/Core.psm1 | 1290-1371 | Save-Setting (persistent key-value store) |
| Extensions/Core.psm1 | 1425-1498 | Get-Setting (persistent key-value retrieval) |

### Wrapp GUI
| File | Key Lines | Purpose |
|---|---|---|
| Services/MsalAuthService.cs | 169-175 | GetCachedAccountsAsync (MSAL cache read) |
| Services/MsalAuthService.cs | 181-199 | AcquireTokenForAccountAsync (silent switch) |
| Services/MsalAuthService.cs | 137-153 | SignOutAsync (clear all cached accounts) |
| Services/MsalAuthService.cs | 155-171 | ForgetAccountAsync (remove single account) |
| Services/MsalAuthService.cs | 205-227 | ResolveOrganizationNameAsync (Graph API) |
| ViewModels/AccountViewModel.cs | 17-26 | CachedAccountItem model |
| ViewModels/AccountViewModel.cs | 578-613 | RefreshCachedAccountsAsync (config-based lookup) |
| ViewModels/AccountViewModel.cs | 275-302 | SwitchAccountAsync (silent auth) |
| ViewModels/AccountViewModel.cs | 471-524 | ApplyTokenResultAsync (resolves org name but doesn't persist) |
| Views/MainWindow.xaml | 301-388 | Not-signed-in section (cached accounts + tenant switcher) |
| Views/MainWindow.xaml | 390-484 | Signed-in section (cached accounts) |
| Views/MainWindow.xaml | 486-561 | Config-based tenant switcher (WRONG) |
