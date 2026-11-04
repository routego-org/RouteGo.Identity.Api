# CAP-AUTH-003 — Resend verification email

- Capability: CAP-AUTH-003
- Canonical spec: `capabilities/authentication/features/email-verification.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/ResendVerificationEmail/`

## Contract

Implemented verbatim from `contracts/authentication/email-verification.yaml`.

- Operation: `resendVerificationEmail`
- Route / verb: `POST /auth/verificationEmails` — public (`security: []`)
- Request: `VerificationEmailRequest` (`email`)
- Response: `200` `VerificationEmailSent` (`email`, `linkExpiresAt`)
- Problem codes: `VALIDATION_FAILED` (400), `EMAIL_NOT_REGISTERED` (422), `EMAIL_ALREADY_VERIFIED`
  (422, `accountStatus`), `RATE_LIMITED` (429, `retryAfterSeconds`)

## Acceptance criteria

Each criterion maps to one test in `ResendVerificationEmail.Tests.cs`.

- [x] AC-7: resend issues a new link and voids the old ones — `ResendVerificationEmail_PendingAccount_IssuesANewLinkAndVoidsTheOldOnes`
- [x] AC-8: email with no account — `ResendVerificationEmail_EmailWithNoAccount_IsRefusedAndSendsNothing`
- [x] AC-9: email already verified — `ResendVerificationEmail_AccountAlreadyActive_IsRefusedCarryingItsStatus`
- [x] AC-10: one resend per minute — `ResendVerificationEmail_ResendAccepted30SecondsAgo_IsRateLimitedFor30Seconds`
- [x] AC-11: five resends per 24 hours — `ResendVerificationEmail_FiveResendsInTheLastDay_IsRateLimitedUntilTheOldestAgesOut`
- [x] AC-12: malformed email — `ResendVerificationEmail_MalformedEmail_IsRefusedAsValidationFailed`

## Invariants touched

- Resend limits: `Domain/ResendLimits.cs`. Accepted resends are the account's links other than its
  first (the registration or application link); refused requests issue no link, so never count.
- Only the newest link works: earlier unused links are voided in the same transaction.
- Links: `Common/Accounts/VerificationLinks.cs`; a resend records a fresh `UserRegistered`.

## Notes

- `email` in the response is the account's stored email.
- All link times and the limit checks use the database clock.
- Refusals and rate-limit hits are logged at Warning with the account id, never the email or token.
