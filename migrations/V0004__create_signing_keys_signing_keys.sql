-- =============================================================================
-- V0004__create_signing_keys_signing_keys.sql
-- The signing_keys schema, and each token-signing key with where it is in its rotation.
-- =============================================================================
-- Implements: data-model/signing-keys/signing-key.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: natural key. The entity is identified by key_id, a code the platform engineer
-- chooses and every token carries, so there is no system-assigned id column.
-- The private half of each key is never stored here; it lives in Secrets Manager under
-- routego/identity/jwt-signing-key/<key_id>.

BEGIN;

CREATE SCHEMA IF NOT EXISTS signing_keys;

CREATE TABLE signing_keys.signing_keys (
    key_id           text        NOT NULL,
    public_key       text        NOT NULL,
    state            text        NOT NULL,
    introduced_at    timestamptz NOT NULL,
    introduced_by    text        NOT NULL,
    activated_at     timestamptz,
    activated_by     text,
    superseded_at    timestamptz,
    retired_at       timestamptz,
    retired_by       text,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    -- Rule: key_id is unique and never reused, even after retirement (keys are never deleted).
    CONSTRAINT pk_signing_keys PRIMARY KEY (key_id),
    -- Rule: state is one of Introduced, Active, Previous, Retired.
    CONSTRAINT ck_signing_keys_state CHECK (state IN ('Introduced', 'Active', 'Previous', 'Retired')),
    CONSTRAINT ck_signing_keys_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_signing_keys_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "Exactly one key is Active" - the database guarantees at most one; that one exists is
-- the rotation's own rule (activate always replaces the active key in the same step).
CREATE UNIQUE INDEX uq_signing_keys_active_state ON signing_keys.signing_keys (state) WHERE state = 'Active';

-- Rule: "at most one is Introduced".
CREATE UNIQUE INDEX uq_signing_keys_introduced_state ON signing_keys.signing_keys (state) WHERE state = 'Introduced';

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('signing_keys.signing_keys', 'key_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE signing_keys.signing_keys IS 'data-model/signing-keys/signing-key.md';

COMMIT;
