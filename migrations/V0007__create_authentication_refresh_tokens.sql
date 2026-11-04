-- =============================================================================
-- V0007__create_authentication_refresh_tokens.sql
-- One refresh token Identity issued. Tokens descending from one sign-in form a chain.
-- =============================================================================
-- Implements: data-model/authentication/refresh-token.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute refresh_token_id.

BEGIN;

CREATE TABLE authentication.refresh_tokens (
    refresh_token_id uuid        NOT NULL DEFAULT gen_random_uuid(),
    user_id          uuid        NOT NULL,
    chain_id         uuid        NOT NULL,
    token_hash       text        NOT NULL,
    issued_at        timestamptz NOT NULL,
    expires_at       timestamptz NOT NULL,
    replaced_by      uuid,
    revoked_at       timestamptz,
    revoke_reason    text,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_refresh_tokens PRIMARY KEY (refresh_token_id),
    -- Relationship: belongs to one User.
    CONSTRAINT fk_refresh_tokens_user_id FOREIGN KEY (user_id) REFERENCES authentication.users (user_id),
    -- Relationship: may be replaced by one later RefreshToken of the same chain.
    CONSTRAINT fk_refresh_tokens_replaced_by
        FOREIGN KEY (replaced_by) REFERENCES authentication.refresh_tokens (refresh_token_id),
    -- Rule: revoke_reason is one of SignedOut, ReuseDetected, AccountSuspended.
    CONSTRAINT ck_refresh_tokens_revoke_reason CHECK (revoke_reason IN ('SignedOut', 'ReuseDetected', 'AccountSuspended')),
    CONSTRAINT ck_refresh_tokens_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_refresh_tokens_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Refresh and sign-out look a token up by the hash of the token presented.
CREATE INDEX ix_refresh_tokens_token_hash ON authentication.refresh_tokens (token_hash);

-- Reuse detection and sign-out revoke a whole chain; suspension revokes every token of an account.
CREATE INDEX ix_refresh_tokens_chain_id ON authentication.refresh_tokens (chain_id);
CREATE INDEX ix_refresh_tokens_user_id ON authentication.refresh_tokens (user_id);
CREATE INDEX ix_refresh_tokens_replaced_by ON authentication.refresh_tokens (replaced_by);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('authentication.refresh_tokens', 'refresh_token_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE authentication.refresh_tokens IS 'data-model/authentication/refresh-token.md';

COMMIT;
