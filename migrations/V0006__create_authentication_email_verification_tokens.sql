-- =============================================================================
-- V0006__create_authentication_email_verification_tokens.sql
-- One verification link Identity issued to an account. Only a hash of its token is kept.
-- =============================================================================
-- Implements: data-model/authentication/email-verification-token.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute token_id.

BEGIN;

CREATE TABLE authentication.email_verification_tokens (
    token_id         uuid        NOT NULL DEFAULT gen_random_uuid(),
    user_id          uuid        NOT NULL,
    token_hash       text        NOT NULL,
    issued_at        timestamptz NOT NULL,
    expires_at       timestamptz NOT NULL,
    used_at          timestamptz,
    voided_at        timestamptz,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_email_verification_tokens PRIMARY KEY (token_id),
    -- Relationship: belongs to one User.
    CONSTRAINT fk_email_verification_tokens_user_id
        FOREIGN KEY (user_id) REFERENCES authentication.users (user_id),
    CONSTRAINT ck_email_verification_tokens_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_email_verification_tokens_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Verification looks a link up by the hash of the token it carries.
CREATE INDEX ix_email_verification_tokens_token_hash ON authentication.email_verification_tokens (token_hash);

-- Resend voids an account's earlier links and counts its recent ones.
CREATE INDEX ix_email_verification_tokens_user_id ON authentication.email_verification_tokens (user_id, issued_at);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('authentication.email_verification_tokens', 'token_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE authentication.email_verification_tokens IS 'data-model/authentication/email-verification-token.md';

COMMIT;
