# Account Popup UX Rework

## Requested Changes

### 1. DevOps: Show full app display name instead of truncated GUID
- **Current:** `Browser | 872cd9fa... | 2h 15m remaining`
- **Desired:** `Browser | Visual Studio | 2h 15m remaining`
- The Visual Studio client ID (`872cd9fa-d31f-45e0-9eab-6e460a02d1f1`) should resolve to its display name
- Store a mapping of known client IDs to display names in a static dictionary

### 2. DevOps: Clickable entry to show token details
- When clicking the DevOps account card, show the same Access Token / Id Token detail dialogs as Graph auth
- Reuse the existing `ShowAccessTokenDetailsCommand` pattern
- DevOps uses the same `MsalTokenResult` model so the JWT decode logic is identical

### 3. Graph tenants: Show auth summary even when not selected/signed-in
- Each cached account in the popup should show: auth method | app name | token life or "Expired"
- Currently cached accounts show only username + tenant name
- Need to track per-account token metadata (expiry, flow, client ID)
- This requires storing `MsalTokenResult` per cached account or at least the expiry

### 4. Title bar "Not Signed In" should show token status at a glance
- Even before clicking, the operator should see if tokens are valid
- Could show a small status indicator (green dot = valid, red = expired) next to "Not Signed In"

### 5. Refresh button next to token life
- Already exists for the signed-in Graph tenant (RefreshTokenCommand)
- Need to add for DevOps section
- Updates the timespan display after obtaining a new token

## Implementation Notes

- `ApplyDevOpsToken()` in AccountViewModel line 467 formats the detail string
- Known client IDs: `14d82eec...` = "Microsoft Graph PowerShell", `872cd9fa...` = "Visual Studio"
- The cached accounts list is populated from `MsalAuthService.GetCachedAccountsAsync()`
- IAccount objects have limited metadata (username, homeAccountId) -- no token expiry
- To show per-account token status, we'd need to try `AcquireTokenSilent` for each cached account
