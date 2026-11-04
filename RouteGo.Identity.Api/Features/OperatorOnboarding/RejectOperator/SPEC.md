# CAP-OPR-005 — RejectOperator

- Capability: CAP-OPR-005
- Canonical spec: `capabilities/operator-onboarding/features/operator-application-review.md` (spec 0.1.0)
- Area: OperatorOnboarding
- Slice: `Features/OperatorOnboarding/RejectOperator/`

## Contract

Implemented verbatim from `contracts/operator-onboarding/operator-application.yaml`.

- Operation: `rejectOperator`
- Route / verb: `POST /operators/admin/applications/{operatorId}/reject` — bearer access token, role Admin
- Request: `ApplicationRejection` (`reason`)
- Response: `200` `OperatorApplication`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404),
  `APPLICATION_NOT_PENDING` (409, with `currentStatus`)

## Acceptance criteria

Each criterion maps to tests in `RejectOperator.Tests.cs`.

- [x] AC-7: reject; account Rejected; OperatorRejected and AuditRecorded; GSTIN freed — `RejectOperator_PendingApplication_RejectsRecordsBothEventsAndFreesTheGstin`
- [x] AC-8: already decided by another admin — `RejectOperator_AlreadyApprovedByAnotherAdmin_IsRefusedAndChangesNothing`
- [x] AC-9: not yet verified — `RejectOperator_EmailNotYetVerified_IsRefusedCarryingPendingVerification`
- [x] AC-10: unknown operator — `RejectOperator_UnknownIdentifier_IsNotFound`
- [x] AC-11: reason rules — `RejectOperator_ReasonOutsideTenToFiveHundred_IsRefusedNamingReason`, `RejectOperator_NoReason_IsRefusedNamingReason`
- [x] AC-12: only admins — `RejectOperator_SignedInOperator_IsForbidden`
- [x] AC-13: must be signed in — `RejectOperator_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- Reason 10–500 characters: `Domain/OperatorApplicationRules.cs`.
- A rejected application stops holding its GSTIN (the partial unique index covers pending and approved only).
- The rejection's `reviewed_at` starts the 7-day re-application wait.

## Notes

- OperatorRejected data: `operatorId`, `email`, `reason`. AuditRecorded: entity `OperatorApplication`,
  details `reason`. The signed-in admin is the audit actor.
