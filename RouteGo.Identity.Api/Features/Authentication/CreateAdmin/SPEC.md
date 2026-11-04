# CAP-AUTH-008 — CreateAdmin

- Capability: CAP-AUTH-008
- Canonical spec: `capabilities/authentication/features/admin-account-creation.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/CreateAdmin/`

## Contract

Implemented verbatim from `contracts/authentication/user.yaml`.

- Operation: `createAdmin`
- Route / verb: `POST /auth/admin/users` — bearer access token, role Admin
- Request: `AdminCreation` (`email`, `password`, `fullName`, optional `phone`)
- Response: `201` `UserSummary`, with `Location`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `EMAIL_ALREADY_REGISTERED` (409)

## Acceptance criteria

Each criterion maps to tests in `CreateAdmin.Tests.cs`.

- [x] AC-1: admin creates an admin (Active, verified now, AdminCreated audit only, can sign in) — `CreateAdmin_NewEmail_CreatesAnActiveVerifiedAdminAndRecordsOnlyTheAudit`
- [x] AC-2: email already registered — `CreateAdmin_EmailAlreadyACustomer_IsRefusedAndRecordsNothing`
- [x] AC-3: password and field rules — `CreateAdmin_PasswordOrNameBreaksTheRules_IsRefusedNamingTheField`
- [x] AC-4: only admins — `CreateAdmin_SignedInOperator_IsForbidden`
- [x] AC-5: must be signed in — `CreateAdmin_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- Password, email, name and phone rules: `Domain/AccountRules.cs`.
- Email uniqueness ignoring case: the `uq_users_email` index makes the check atomic.

## Notes

- The creating admin is the audit actor (the scoped writer connection) and the AuditRecorded
  event's `actorId`; `details` carries the new admin's email.
- `Location` is `/auth/admin/users/{userId}`, as for registration; no operation serves that address
  yet (raised upstream: A-1 in `specs/spec-drift-2026-10-08.md`).
