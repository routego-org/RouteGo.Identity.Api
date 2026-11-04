-- =============================================================================
-- V0008__create_authentication_account_suspensions.sql
-- One period during which an admin suspended an account, and its reactivation once lifted.
-- =============================================================================
-- Implements: data-model/authentication/account-suspension.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute suspension_id.

BEGIN;

CREATE TABLE authentication.account_suspensions (
    suspension_id     uuid        NOT NULL DEFAULT gen_random_uuid(),
    user_id           uuid        NOT NULL,
    suspended_at      timestamptz NOT NULL,
    suspended_by      uuid        NOT NULL,
    suspend_reason    text        NOT NULL,
    reactivated_at    timestamptz,
    reactivated_by    uuid,
    reactivate_reason text,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_account_suspensions PRIMARY KEY (suspension_id),
    -- Relationship: belongs to one User (the suspended account).
    CONSTRAINT fk_account_suspensions_user_id FOREIGN KEY (user_id) REFERENCES authentication.users (user_id),
    -- Relationship: names the admin User who suspended and, later, the one who reactivated.
    CONSTRAINT fk_account_suspensions_suspended_by FOREIGN KEY (suspended_by) REFERENCES authentication.users (user_id),
    CONSTRAINT fk_account_suspensions_reactivated_by FOREIGN KEY (reactivated_by) REFERENCES authentication.users (user_id),
    -- Rule: a reason is 10-500 characters.
    CONSTRAINT ck_account_suspensions_suspend_reason_length CHECK (char_length(suspend_reason) BETWEEN 10 AND 500),
    CONSTRAINT ck_account_suspensions_reactivate_reason_length
        CHECK (reactivate_reason IS NULL OR char_length(reactivate_reason) BETWEEN 10 AND 500),
    -- Rule: reactivated_at, reactivated_by and reactivate_reason are set together.
    CONSTRAINT ck_account_suspensions_reactivation_complete CHECK (
        (reactivated_at IS NULL AND reactivated_by IS NULL AND reactivate_reason IS NULL)
        OR (reactivated_at IS NOT NULL AND reactivated_by IS NOT NULL AND reactivate_reason IS NOT NULL)),
    CONSTRAINT ck_account_suspensions_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_account_suspensions_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "An account has at most one suspension without reactivated_at - the current one."
-- The index also serves reactivation, which looks up the account's current suspension.
CREATE UNIQUE INDEX uq_account_suspensions_current
    ON authentication.account_suspensions (user_id) WHERE reactivated_at IS NULL;

CREATE INDEX ix_account_suspensions_user_id ON authentication.account_suspensions (user_id);
CREATE INDEX ix_account_suspensions_suspended_by ON authentication.account_suspensions (suspended_by);
CREATE INDEX ix_account_suspensions_reactivated_by ON authentication.account_suspensions (reactivated_by);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('authentication.account_suspensions', 'suspension_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE authentication.account_suspensions IS 'data-model/authentication/account-suspension.md';

COMMIT;
