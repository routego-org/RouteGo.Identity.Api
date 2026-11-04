# CAP-AUTH-005 — RefreshSession

- Capability: CAP-AUTH-005
- Canonical spec: `capabilities/authentication/features/session-refresh-and-sign-out.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/RefreshSession/`

## Contract

Implemented verbatim from `contracts/authentication/session.yaml`.

- Operation: `refreshSession`
- Route / verb: `POST /auth/sessions/refresh` — public (`security: []`); the refresh token is the credential
- Request: `RefreshRequest` (`refreshToken`)
- Response: `200` `TokenPair`
- Problem codes: `VALIDATION_FAILED` (400), `REFRESH_TOKEN_INVALID` (401), `REFRESH_TOKEN_REUSED` (401)

## Acceptance criteria

Each criterion maps to tests in `RefreshSession.Tests.cs`.

- [x] AC-1: refresh rotates the token within the same chain, 7 days from its own issue — `RefreshSession_UnusedToken_RotatesWithinTheSameChain`
- [x] AC-2: a reused token revokes the chain, is logged, and leaves other sessions alone — `RefreshSession_AlreadyExchangedToken_RevokesTheChainButNotOtherSessions`
- [x] AC-3: unknown, expired or revoked token — `RefreshSession_TokenNeverIssued_IsInvalid`, `RefreshSession_ExpiredToken_IsInvalid`
- [x] AC-4: account suspended after sign-in — `RefreshSession_TokenRevokedBySuspension_IsInvalid` (suspension revokes the tokens; end to end with `SuspendUser`)
- [x] AC-5: empty refresh token — `RefreshSession_EmptyToken_IsRefusedAsValidationFailed`

## Invariants touched

- Sessions: `Common/Sessions/SessionIssuer` — the new token continues the presented token's chain.
- Refresh tokens are stored as a SHA-256 hash only (`Common/Security/OpaqueToken`).

## Notes

- Reuse is checked before revoked/expired: a token that was already exchanged means it was copied,
  so the whole chain ends even if that token has since expired or been revoked.
- The account owning the token is the audit actor; the token row is locked for the exchange so two
  concurrent refreshes of the same token cannot both succeed.
