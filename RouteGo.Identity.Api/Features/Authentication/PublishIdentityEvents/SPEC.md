# CAP-AUTH-011 — Publish identity events

- Capability: CAP-AUTH-011
- Canonical spec: `capabilities/authentication/features/identity-event-publishing.md` (spec 0.1.0)
- Area: Authentication
- Slice: `Features/Authentication/PublishIdentityEvents/`

## Contract

`kind: worker` — no HTTP operation. Trigger `schedule: every 5 seconds`, run in-process by
`PublishIdentityEventsService` (a `BackgroundService`); one run is `PublishIdentityEventsHandler.HandleAsync`.

- Reads: `authentication.identity_events` rows with no `delivered_at`, oldest `recorded_at` first.
- Delivers to: `IdentityEventsTopicArn` (UserRegistered, OperatorApproved, OperatorRejected) or
  `AuditEventsTopicArn` (AuditRecorded), with message attribute `eventType` and the RouteGo envelope
  (`eventId`, `eventType`, `version` 1, `occurredAt` = recorded time, `correlationId`, `producer`
  `identity-service`, `data`).
- Emits: `EVENT_PUBLISH_DELAYED` — a worker code, raised as one Error log line per event
  (`EVENT_PUBLISH_DELAYED: identity event {eventId} of type {eventType} recorded at {recordedAt} …`);
  a CloudWatch metric filter on `/routego/identity` turns it into an alarm on `routego-alarms`.

## Acceptance criteria

Each criterion maps to one test in `PublishIdentityEvents.Tests.cs`.

- [x] AC-1: a recorded event is delivered with the envelope and correlation ID, and marked delivered — `HandleAsync_RecordedEvent_DeliversEnvelopeAndMarksDelivered`
- [x] AC-2: audit events go to the audit stream — `HandleAsync_AuditRecordedEvent_GoesToTheAuditStream`
- [x] AC-3: order is preserved — `HandleAsync_ThreeUndeliveredEvents_DeliversThemInRecordedOrder`
- [x] AC-4: a delivered event is not delivered again — `HandleAsync_EventAlreadyDelivered_IsNotDeliveredAgain`
- [x] AC-5: delivery resumes after an outage, in order, nothing lost — `HandleAsync_MessagingUnavailable_KeepsEventsThenDeliversInOrderOnRecovery`
- [x] AC-6: a delivered-but-unconfirmed event repeats with the same event ID — `HandleAsync_DeliveryConfirmationLost_RedeliversWithTheSameEventId`
- [x] AC-7: alert once after 5 minutes undelivered — `HandleAsync_EventUndeliveredForOverFiveMinutes_RaisesTheAlertOnce` (and the under-5-minutes counterpart)

## Invariants touched

- Stream routing (AuditRecorded → AuditEvents, everything else → IdentityEvents) is a table CHECK
  (`ck_identity_events_stream_routing`), so a misrouted event cannot be recorded at all.
- An undelivered event holds back every later one: a run stops at the first failed delivery.
- One publisher at a time across instances (transaction-scoped advisory lock), so order holds when
  more than one container runs.

## Notes

- Writes are attributed to the system actor with action `job:PublishIdentityEvents`.
- Order is by `recorded_at` among committed events; events from two transactions committing
  concurrently are delivered in the order they become visible.
