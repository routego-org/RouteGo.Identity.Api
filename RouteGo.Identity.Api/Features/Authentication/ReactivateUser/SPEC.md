# CAP-AUTH-010 — ReactivateUser

- Capability: CAP-AUTH-010
- Canonical spec: `capabilities/authentication/features/account-suspension.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/ReactivateUser/`

## Contract

Implemented verbatim from `contracts/authentication/user.yaml`.

- Operation: `reactivateUser`
- Route / verb: `POST /auth/admin/users/{userId}/reactivate` — bearer access token, role Admin
- Request: `StatusChangeReason` (`reason`)
- Response: `200` `UserSummary`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `USER_NOT_SUSPENDED` (409, with `currentStatus`)

## Acceptance criteria

Each criterion maps to tests in `ReactivateUser.Tests.cs`.

- [x] AC-6: reason rules — `ReactivateUser_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason`, `ReactivateUser_NoReason_IsRefusedNamingReason`
- [x] AC-7: reactivate; UserReactivated audit; signs in again, no earlier session restored — `ReactivateUser_SuspendedCustomer_IsActiveAgainAndSignsInWithoutTheOldSession`
- [x] AC-8: only Suspended accounts — `ReactivateUser_ActiveAccount_IsRefusedCarryingStatusActive`
- [x] AC-9: unknown account — `ReactivateUser_UnknownIdentifier_IsNotFound`
- [x] AC-10: only admins — `ReactivateUser_SignedInCustomer_IsForbidden`
- [x] AC-11: must be signed in — `ReactivateUser_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- `Domain/SuspensionRules.cs`: the reason length (shared with suspension).

## Notes

- Closes the account's open suspension (who, when, why) in the same transaction as the status
  change and the AuditRecorded event.
