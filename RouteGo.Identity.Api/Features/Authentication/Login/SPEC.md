# CAP-AUTH-004 — Login

- Capability: CAP-AUTH-004
- Canonical spec: `capabilities/authentication/features/sign-in.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/Login/`

## Contract

Implemented verbatim from `contracts/authentication/session.yaml`.

- Operation: `login`
- Route / verb: `POST /auth/sessions` — public (`security: []`)
- Request: `Credentials` (`email`, `password`)
- Response: `200` `TokenPair` (`accessToken`, `accessTokenExpiresAt`, `refreshToken`, `refreshTokenExpiresAt`, `tokenType`)
- Problem codes: `VALIDATION_FAILED` (400), `INVALID_CREDENTIALS` (401), `EMAIL_NOT_VERIFIED`,
  `ACCOUNT_PENDING_APPROVAL`, `ACCOUNT_REJECTED`, `ACCOUNT_SUSPENDED`, `ACCOUNT_LOCKED` (with `lockedUntil`) (403)

## Acceptance criteria

Each criterion maps to tests in `Login.Tests.cs`.

- [x] AC-1: successful sign-in (15-minute access token, 7-day refresh token, new chain, count reset) — `Login_ActiveCustomer_ReturnsTokensAndResetsFailures`
- [x] AC-2: operator token carries operatorId — `Login_ActiveOperator_TokenCarriesOperatorId`
- [x] AC-3: unknown email or wrong password, same answer — `Login_UnknownEmailOrWrongPassword_AnswersInvalidCredentialsTheSameWay`
- [x] AC-4 to AC-7: correct password, account not Active — `Login_CorrectPasswordButAccountNotActive_IsRefusedWithItsReason`
- [x] AC-8: status never revealed without the password; the failure counts — `Login_WrongPasswordOnAnInactiveAccount_NeverRevealsTheStatusAndCountsTheFailure`
- [x] AC-9: fifth failure locks for 15 minutes, logged — `Login_FifthFailureWithinTenMinutes_LocksForFifteenMinutesAndLogsIt`
- [x] AC-10: locked means locked, lock not extended — `Login_LockedAccountWithTheRightPassword_IsStillRefusedAndTheLockIsNotExtended`
- [x] AC-11: lock ends on time — `Login_AfterTheLockEnds_SignsInAndResetsTheCount`
- [x] AC-12: failures outside the window do not add up — `Login_FailuresStartedOutsideTheWindow_DoNotAddUpToALock`
- [x] AC-13: malformed request counts nothing — `Login_MalformedRequest_IsRefusedAndCountsNothing`

## Invariants touched

- Lockout: `Domain/Lockout.cs` (shared with operator re-application). The window is the entity's
  `first_failed_sign_in_at`: a failure more than 10 minutes after it starts a new window.
- Sessions: `Common/Sessions/SessionIssuer` — each sign-in starts a new token chain.

## Notes

- The account is the audit actor; the attempt runs under a row lock so concurrent failures count exactly.
- An unknown email still verifies a password hash, so timing does not reveal which emails have accounts.
- Lockout times use the database clock.
- Failed sign-ins and lockouts are logged at Warning with the account id, never the password.
