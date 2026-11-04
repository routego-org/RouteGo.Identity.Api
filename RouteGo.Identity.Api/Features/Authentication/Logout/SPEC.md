# CAP-AUTH-006 — Logout

- Capability: CAP-AUTH-006
- Canonical spec: `capabilities/authentication/features/session-refresh-and-sign-out.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/Logout/`

## Contract

Implemented verbatim from `contracts/authentication/session.yaml`.

- Operation: `logout`
- Route / verb: `POST /auth/sessions/revoke` — bearer access token required
- Request: `RefreshRequest` (`refreshToken`)
- Response: `204`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401)

## Acceptance criteria

Each criterion maps to tests in `Logout.Tests.cs`.

- [x] AC-6: sign out this device; other devices stay signed in — `Logout_ThisDevice_EndsItsSessionAndLeavesOtherDevicesSignedIn`
- [x] AC-7: repeated sign-out, or someone else's token, is harmless — `Logout_RepeatedOrWithSomeoneElsesToken_IsHarmless`
- [x] AC-8: needs a valid access token — `Logout_WithoutAnAccessToken_IsUnauthorized`, `Logout_WithAnExpiredAccessToken_IsUnauthorized`
- [x] AC-9: empty refresh token — `Logout_EmptyRefreshToken_IsRefusedAsValidationFailed`

## Invariants touched

- Ownership is part of the revoking `WHERE` clause: a token that is not the caller's revokes nothing.

## Notes

- Access tokens cannot be revoked; one issued before sign-out stays valid until it expires.
