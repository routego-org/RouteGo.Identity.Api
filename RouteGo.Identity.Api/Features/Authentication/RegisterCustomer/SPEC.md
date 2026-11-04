# CAP-AUTH-001 — Register customer

- Capability: CAP-AUTH-001
- Canonical spec: `capabilities/authentication/features/customer-registration.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/RegisterCustomer/`

## Contract

Implemented verbatim from `contracts/authentication/user.yaml`.

- Operation: `registerCustomer`
- Route / verb: `POST /auth/registrations` — public (`security: []`)
- Request: `CustomerRegistration` (`email`, `password`, `fullName`, optional `phone`)
- Response: `201` `UserSummary`, `Location: /auth/admin/users/{userId}`
- Problem codes: `VALIDATION_FAILED` (400, `errors` keyed by camelCase field), `EMAIL_ALREADY_REGISTERED` (409)

## Event

Records `UserRegistered` on the identity events stream in the same transaction as the account and
its verification token. Data: `userId`, `email`, `name` (full name), `role` (`Customer`, the
system-wide spelling), `verificationLink`.

**Verification link format (agreed; the Web Portal must serve it):**
`{PortalBaseUrl}/auth/verify?token=<token>` — e.g. `http://localhost:8080/auth/verify?token=…`
(the Portal's verify page, CAP-UI-AUTH-002 in the RouteGo.App spec).
The token is 256 random bits, base64url; only its SHA-256 hash is stored; it expires after
`VerificationTokenHours` (SSM `verification-token-hours`, default 24).

## Acceptance criteria

Each criterion maps to one test in `RegisterCustomer.Tests.cs`.

- [x] AC-1: successful registration — `RegisterCustomer_NewEmail_CreatesPendingCustomerAndRecordsUserRegistered`
- [x] AC-2: phone is optional — `RegisterCustomer_WithoutPhone_CreatesTheAccountWithNoPhone`
- [x] AC-3: email already registered, any role, any case — `RegisterCustomer_EmailRegisteredInAnyRoleAndCase_IsRefusedAndCreatesNothing`
- [x] AC-4: password policy — `RegisterCustomer_PasswordBreaksThePolicy_IsRefusedNamingPassword`
- [x] AC-5: name and phone validation — `RegisterCustomer_InvalidNameOrPhone_IsRefusedNamingTheField`
- [x] AC-6: succeeds while email delivery is down — `RegisterCustomer_MessagingUnavailable_SucceedsAndKeepsTheEventForLaterDelivery`
- [x] AC-7: verification link content — `RegisterCustomer_Success_IssuesASingleUseLinkValidFor24HoursAndKeepsOnlyItsHash`

## Invariants touched

- Password, name and phone rules: `Domain/AccountRules.cs`; email uniqueness ignoring case:
  `uq_users_email` on `lower(email)`.
- Verification links: `Common/Accounts/VerificationLinks.cs` (shared with resend and operator application).

## Notes

- No signed-in principal exists: the new account is the audit actor of its own rows.
- `Location` names the admin address the contract describes; no GET-by-id operation exists in the
  contract, so the address is informational.
