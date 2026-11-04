# CAP-OPR-003 — ListOperatorApplications

- Capability: CAP-OPR-003
- Canonical spec: `capabilities/operator-onboarding/features/operator-application-review.md` (spec 0.1.0)
- Area: OperatorOnboarding
- Slice: `Features/OperatorOnboarding/ListOperatorApplications/`

## Contract

Implemented verbatim from `contracts/operator-onboarding/operator-application.yaml`.

- Operation: `listOperatorApplications`
- Route / verb: `GET /operators/admin/applications` — bearer access token, role Admin
- Request: query `status` (default `pendingApproval`), `page`, `pageSize`
- Response: `200` `OperatorApplicationListResponse`
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403)

## Acceptance criteria

Each criterion maps to tests in `ListOperatorApplications.Tests.cs`.

- [x] AC-1: default queue, oldest first, no unverified — `ListOperatorApplications_NoStatus_ReturnsPendingApprovalOldestFirstWithoutUnverified`
- [x] AC-2: earlier rejections are shown — `ListOperatorApplications_OperatorThatReapplied_ShowsLatestWithTheEarlierRejection`
- [x] AC-3: filter by status and page — `ListOperatorApplications_TwentyFiveRejectedSecondPage_ReturnsTheLastFiveOldestFirst`
- [x] AC-4: empty queue — `ListOperatorApplications_NothingAwaitingReview_ReturnsAnEmptyPage`
- [x] AC-5: invalid filter — `ListOperatorApplications_UnknownStatus_IsRefusedNamingStatus`
- [x] AC-12: only admins — `ListOperatorApplications_SignedInOperator_IsForbidden`
- [x] AC-13: must be signed in — `ListOperatorApplications_WithoutAnAccessToken_IsUnauthorized`

## Invariants touched

- Each operator appears once, as its latest application (`DISTINCT ON (operator_id)` by submission time).
- Response shape and earlier rejections: `Common/OperatorApplications/OperatorApplicationReader`.
