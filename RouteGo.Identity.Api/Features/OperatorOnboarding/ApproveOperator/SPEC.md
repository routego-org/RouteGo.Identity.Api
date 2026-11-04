# CAP-OPR-004 — ApproveOperator

- Capability: CAP-OPR-004
- Canonical spec: `capabilities/operator-onboarding/features/operator-application-review.md` (spec 0.1.0)
- Area: OperatorOnboarding
- Slice: `Features/OperatorOnboarding/ApproveOperator/`

## Contract

Implemented verbatim from `contracts/operator-onboarding/operator-application.yaml`.

- Operation: `approveOperator`
- Route / verb: `POST /operators/admin/applications/{operatorId}/approve` — bearer access token, role Admin
- Request: none
- Response: `200` `OperatorApplication`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404), `APPLICATION_NOT_PENDING` (409, with `currentStatus`)
- verb-justified: POST with no body is the contract's action endpoint; it writes the decision.

## Acceptance criteria

Each criterion maps to tests in `ApproveOperator.Tests.cs`.

- [x] AC-6: approve; account Active; OperatorApproved and AuditRecorded; signs in with operatorId — `ApproveOperator_PendingApplication_ApprovesActivatesAndRecordsBothEvents`
- [x] AC-8: already decided by another admin — `ApproveOperator_AlreadyApprovedBySecondAdmin_IsRefusedAndRecordsNothingMore`
- [x] AC-9: not yet verified — `ApproveOperator_EmailNotYetVerified_IsRefusedCarryingPendingVerification`
- [x] AC-10: unknown operator (or not an operator) — `ApproveOperator_IdentifierThatIsNotAnOperator_IsNotFound`
- [x] AC-12: only admins — `ApproveOperator_SignedInOperator_IsForbidden`
- [x] AC-13: must be signed in — `ApproveOperator_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- Decisions only from PendingApproval, on the operator's latest application.
- The operator's account row is locked first, so concurrent decisions are serialized.

## Notes

- OperatorApproved data: `operatorId`, `companyName`, `email`. AuditRecorded: entity `OperatorApplication`,
  details `companyName`, `gstin`. The signed-in admin is the audit actor.
