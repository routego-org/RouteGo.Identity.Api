# CAP-OPR-001 — ApplyAsOperator

- Capability: CAP-OPR-001
- Canonical spec: `capabilities/operator-onboarding/features/operator-application.md` (spec 0.1.0)
- Area: OperatorOnboarding
- Slice: `Features/OperatorOnboarding/ApplyAsOperator/`

## Contract

Implemented verbatim from `contracts/operator-onboarding/operator-application.yaml`.

- Operation: `applyAsOperator`
- Route / verb: `POST /operators/applications` — public (`security: []`)
- Request: `OperatorApplicationRequest` (`email`, `password`, `fullName`, optional `phone`, `companyName`, `contactEmail`, `gstin`)
- Response: `201` `OperatorApplication` (no `Location` in the contract)
- Problem codes: `VALIDATION_FAILED` (400), `EMAIL_ALREADY_REGISTERED` (409), `GSTIN_ALREADY_REGISTERED` (409)

## Acceptance criteria

Each criterion maps to tests in `ApplyAsOperator.Tests.cs`.

- [x] AC-1: successful application — `ApplyAsOperator_NewCompany_CreatesPendingAccountApplicationAndUserRegistered`
- [x] AC-2: verification puts it up for review (end to end through verify and the admin list) — `ApplyAsOperator_ThenVerified_AccountAndApplicationAwaitReviewAndAppearInTheQueue`
- [x] AC-3: email already registered — `ApplyAsOperator_EmailAlreadyACustomer_IsRefusedAndCreatesNothing`
- [x] AC-4: GSTIN held by a pending or approved application — `ApplyAsOperator_GstinHeldByAnotherOperator_IsRefusedAndCreatesNothing`
- [x] AC-5: a rejected application's GSTIN is free — `ApplyAsOperator_GstinOnlyOnARejectedApplication_IsAccepted`
- [x] AC-6: company detail validation — `ApplyAsOperator_InvalidCompanyDetail_IsRefusedNamingTheField`
- [x] AC-7: account field validation — `ApplyAsOperator_ShortPassword_IsRefusedNamingPassword`, `ApplyAsOperator_WithoutFullName_IsRefusedNamingFullName`

## Invariants touched

- Company name, GSTIN format: `Domain/OperatorApplicationRules.cs`; account fields: `Domain/AccountRules.cs`.
- Email and GSTIN uniqueness are the database's partial unique indexes, used through `ON CONFLICT DO NOTHING`,
  so both refusals are atomic under concurrency.
- Response shape: `Common/OperatorApplications/OperatorApplicationReader` (shared by the area's five slices).

## Notes

- Account, application, verification token and UserRegistered (role `Operator`) commit in one transaction;
  the new account is the audit actor.
