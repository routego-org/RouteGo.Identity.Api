# CAP-AUTH-009 — SuspendUser

- Capability: CAP-AUTH-009
- Canonical spec: `capabilities/authentication/features/account-suspension.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/SuspendUser/`

## Contract

Implemented verbatim from `contracts/authentication/user.yaml`.

- Operation: `suspendUser`
- Route / verb: `POST /auth/admin/users/{userId}/suspend` — bearer access token, role Admin
- Request: `StatusChangeReason` (`reason`)
- Response: `200` `UserSummary`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `USER_NOT_ACTIVE` (409, with `currentStatus`), `ADMIN_CANNOT_BE_SUSPENDED` (422)

## Acceptance criteria

Each criterion maps to tests in `SuspendUser.Tests.cs`.

- [x] AC-1: suspend an active customer; both refresh-token chains revoked; UserSuspended audit — `SuspendUser_ActiveCustomerOnTwoDevices_SuspendsEndsBothSessionsAndRecordsTheAudit`
- [x] AC-2: suspended operator cannot sign in — `SuspendUser_ActiveOperator_IsSuspendedAndCanNoLongerSignIn`
- [x] AC-3: admin accounts cannot be suspended — `SuspendUser_ActiveAdmin_IsRefusedAndNothingChanges`
- [x] AC-4: only Active accounts — `SuspendUser_AccountNotActive_IsRefusedCarryingItsStatus`
- [x] AC-5: unknown account — `SuspendUser_UnknownIdentifier_IsNotFound`
- [x] AC-6: reason rules — `SuspendUser_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason`, `SuspendUser_NoReason_IsRefusedNamingReason`
- [x] AC-10: only admins — `SuspendUser_SignedInCustomer_IsForbidden`
- [x] AC-11: must be signed in — `SuspendUser_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- `Domain/SuspensionRules.cs`: the reason length and "admins are never suspended" (shared with reactivation).
- At most one open suspension per account: partial unique index `uq_account_suspensions_current`.

## Notes

- Status change, suspension record, revocation of every refresh token (`AccountSuspended`) and the
  AuditRecorded event commit in one transaction; the account row is locked for it.
- This is what makes refresh's AC-4 hold end to end: AC-1 here refreshes both devices afterwards.
