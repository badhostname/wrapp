# Migrate DevOps Auth from Browser Flow to WAM

## Idea

Switch `DevOpsAuthService` from system browser flow to WAM broker, matching the Graph auth approach. Now that the DPAPI token cache fix is in place (build 0.5.0.0108), WAM refresh tokens persist across app restarts, eliminating the original reason for using browser flow.

## What to change

- Replace browser-based PCA in `DevOpsAuthService` with WAM broker PCA (Visual Studio client ID `872cd9fa-d31f-45e0-9eab-6e460a02d1f1`)
- Add `BeforeAccess`/`AfterAccess` DPAPI hooks to `msal_devops_cache.bin` (same pattern as `MsalAuthService`)
- Keep browser as fallback if WAM fails (same pattern as Graph auth)
- Unified auth UX: both Graph and DevOps use WAM with OS-level credential management

## Benefits

- SSO via PRT -- potentially no interactive sign-in needed if the user is already signed in via Windows
- Consistent UX between Graph and DevOps auth flows
- Simpler architecture (both flows use the same auth pattern)

## Risks

- Two WAM PCAs with different client IDs running simultaneously (untested, should be fine)
- Visual Studio client ID under WAM for DevOps scope is untested -- may behave differently
- If WAM fails, browser fallback is available
