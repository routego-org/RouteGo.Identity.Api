# CAP-AUTH-002 — Verify email

- Capability: CAP-AUTH-002
- Canonical spec: `capabilities/authentication/features/email-verification.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/VerifyEmail/`

## Contract

Implemented verbatim from `contracts/authentication/email-verification.yaml`.

- Operation: `verifyEmail`
- Route / verb: `POST /auth/emailVerifications` — public (`security: []`)
- Request: `EmailVerification` (`token`)
- Response: `200` `EmailVerificationResult` (`userId`, `role`, `status`, `tokens` — a `TokenPair` for a
  customer, null for an operator)
- Problem codes: `VALIDATION_FAILED` (400), `VERIFICATION_LINK_EXPIRED`, `VERIFICATION_LINK_ALREADY_USED`
  (with `accountStatus`), `VERIFICATION_LINK_INVALID` (all 422)

## Acceptance criteria

Each criterion maps to one test in `VerifyEmail.Tests.cs`.

- [x] AC-1: a customer verifies and is signed in (token validated against the active key) — `VerifyEmail_CustomerWithFreshLink_BecomesActiveAndIsSignedIn`
- [x] AC-2: an operator verifies; the account and its application both await review, no tokens —
  `VerifyEmail_OperatorWithFreshLink_AccountAndApplicationAwaitApprovalWithNoTokens` (end to end with
  the review queue in `ApplyAsOperator.Tests.cs`)
- [x] AC-3: expired link — `VerifyEmail_LinkIssued25HoursAgo_IsRefusedAsExpired`
- [x] AC-4: link already used — `VerifyEmail_LinkAlreadyUsed_IsRefusedCarryingTheAccountStatus`
- [x] AC-5: unknown or superseded link — `VerifyEmail_TokenNeverIssued_IsRefusedAsInvalid`, `VerifyEmail_LinkReplacedByANewerOne_IsRefusedAsInvalid`
- [x] AC-6: malformed request — `VerifyEmail_EmptyToken_IsRefusedAsValidationFailed`

## Invariants touched

- `email_verified_at` is set exactly once (the update is guarded by `IS NULL`).
- Sessions are issued by `Common/Sessions/SessionIssuer` exactly as sign-in issues them (RS256 with
  the active key; claims `sub`, `email`, `role`, `operatorId` for operators, `iss`, `aud`, `exp`;
  the token handler also stamps the standard `iat`/`nbf`).

## Notes

- The account the link belongs to is the audit actor (nobody is signed in). It is found with a read
  before the write transaction opens; every decision re-reads the link inside that transaction.
- Expiry and "verified now" use the database clock, the same clock the link times were stored with.
- Refusals are logged at Warning without the token.
