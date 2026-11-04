-- =============================================================================
-- V0003__create_authentication_identity_events.sql
-- The authentication schema, and the table events wait in until they are delivered.
-- =============================================================================
-- Implements: data-model/authentication/identity-event.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's own identity attribute (event_id), which is also the
-- eventId consumers de-duplicate on.

BEGIN;

CREATE SCHEMA IF NOT EXISTS authentication;

CREATE TABLE authentication.identity_events (
    event_id         uuid        NOT NULL DEFAULT gen_random_uuid(),
    event_type       text        NOT NULL,
    stream           text        NOT NULL,
    -- "The event's data": the JSON object placed in the envelope's data member. Kept as text,
    -- the logical type, because it is only ever written whole and read back whole.
    data             text        NOT NULL,
    correlation_id   text        NOT NULL,
    recorded_at      timestamptz NOT NULL,
    delivered_at     timestamptz,
    delay_alerted_at timestamptz,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    -- The *_by_id columns are text (not uuid) - the actor id is an opaque string from the
    -- authenticating issuer, capped at 64 chars by the CHECKs below.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_identity_events PRIMARY KEY (event_id),
    -- Rule: event_type is one of UserRegistered, OperatorApproved, OperatorRejected, AuditRecorded.
    CONSTRAINT ck_identity_events_event_type CHECK (
        event_type IN ('UserRegistered', 'OperatorApproved', 'OperatorRejected', 'AuditRecorded')),
    -- Rule: stream is one of IdentityEvents, AuditEvents.
    CONSTRAINT ck_identity_events_stream CHECK (stream IN ('IdentityEvents', 'AuditEvents')),
    -- Rule: "AuditRecorded to AuditEvents, every other type to IdentityEvents."
    CONSTRAINT ck_identity_events_stream_routing CHECK (
        (event_type = 'AuditRecorded') = (stream = 'AuditEvents')),
    CONSTRAINT ck_identity_events_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_identity_events_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- The publisher reads the undelivered events oldest first on every run; delivered ones are the
-- vast majority and never read again, so the index covers only what is waiting.
CREATE INDEX ix_identity_events_recorded_at_undelivered
    ON authentication.identity_events (recorded_at, event_id)
    WHERE delivered_at IS NULL;

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('authentication.identity_events', 'event_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE authentication.identity_events IS 'data-model/authentication/identity-event.md';

COMMIT;
