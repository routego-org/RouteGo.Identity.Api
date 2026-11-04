# CAP-OPR-002 — ReapplyAsOperator

- Capability: CAP-OPR-002
- Canonical spec: `capabilities/operator-onboarding/features/operator-reapplication.md` (spec 0.1.0)
- Area: OperatorOnboarding
- Slice: `Features/OperatorOnboarding/ReapplyAsOperator/`

## Contract

Implemented verbatim from `contracts/operator-onboarding/operator-application.yaml`.

- Operation: `reapplyAsOperator`
- Route / verb: `POST /operators/reapplications` — public (`security: []`); email and password are the credential
- Request: `OperatorReapplicationRequest` (`email`, `password`, `companyName`, `contactEmail`, `gstin`)
- Response: `201` `OperatorApplication`
- Problem codes: `VALIDATION_FAILED` (400), `INVALID_CREDENTIALS` (401), `ACCOUNT_LOCKED` (403, with `lockedUntil`),
  `OPERATOR_NOT_REJECTED` (409, with `currentStatus`), `GSTIN_ALREADY_REGISTERED` (409),
  `REAPPLICATION_TOO_SOON` (422, with `reapplyAllowedAt`)

## Acceptance criteria

Each criterion maps to tests in `ReapplyAsOperator.Tests.cs`.

- [x] AC-1: successful re-application, earlier rejection kept and listed — `ReapplyAsOperator_RejectedEightDaysAgo_SubmitsANewApplicationForReview`
- [x] AC-2: too soon — `ReapplyAsOperator_RejectedThreeDaysAgo_IsRefusedWithTheTimeItBecomesAllowed`
- [x] AC-3: wrong password counts toward lockout; unknown email — `ReapplyAsOperator_WrongPassword_IsRefusedAndCountsTowardLockout`, `ReapplyAsOperator_EmailWithNoAccount_IsRefusedAsInvalidCredentials`
- [x] AC-4: locked account — `ReapplyAsOperator_LockedAccountWithTheRightPassword_IsRefusedCarryingLockedUntil`
- [x] AC-5: only rejected operators — `ReapplyAsOperator_AccountNotARejectedOperator_IsRefusedCarryingItsStatus`
- [x] AC-6: GSTIN held by another operator — `ReapplyAsOperator_GstinHeldByAnotherOperator_IsRefused`
- [x] AC-7: same GSTIN as its own rejected application — `ReapplyAsOperator_SameGstinAsItsOwnRejectedApplication_IsAccepted`
- [x] AC-8: validation — `ReapplyAsOperator_InvalidCompanyDetail_IsRefusedNamingTheField`

## Invariants touched

- Lockout: `Domain/Lockout.cs` (shared with sign-in). The 7-day wait: `Domain/OperatorApplicationRules.cs`.
- Company detail rules and GSTIN uniqueness as for a first application.

## Notes

- Check order is the spec's: shape → lock → password → status → wait → GSTIN. Only a failed password
  attempt (or a new lock) and a successful re-application are committed; other refusals change nothing.
- A successful re-application clears the failure window, as a successful sign-in does (the spec says
  lockout follows sign-in's rules).
- No event is recorded on re-application.
- The handler's SQL and rows live in `ReapplyAsOperator.Sql.cs` to keep the anchor file within budget.
