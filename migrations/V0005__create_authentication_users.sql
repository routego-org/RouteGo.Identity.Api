-- =============================================================================
-- V0005__create_authentication_users.sql
-- One RouteGo account: a customer, an operator or an admin.
-- =============================================================================
-- Implements: data-model/authentication/user.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute user_id (the token's sub claim).
-- created_at / updated_at are both entity attributes and audit columns: one column each,
-- stamped by the audit trigger.

BEGIN;

CREATE TABLE authentication.users (
    user_id                 uuid        NOT NULL DEFAULT gen_random_uuid(),
    email                   text        NOT NULL,
    password_hash           text        NOT NULL,
    full_name               text        NOT NULL,
    phone                   text,
    role                    text        NOT NULL,
    status                  text        NOT NULL,
    email_verified_at       timestamptz,
    failed_sign_in_count    int         NOT NULL,
    first_failed_sign_in_at timestamptz,
    -- Named verbatim from the entity file ("when the current lockout ends"), not *_at.
    locked_until            timestamptz,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_users PRIMARY KEY (user_id),
    -- Rule: email is at most 254 characters.
    CONSTRAINT ck_users_email_length CHECK (char_length(email) <= 254),
    -- Rule: full name is 2-100 characters.
    CONSTRAINT ck_users_full_name_length CHECK (char_length(full_name) BETWEEN 2 AND 100),
    -- Rule: phone is kept as +91 followed by 10 digits starting 6-9.
    CONSTRAINT ck_users_phone_format CHECK (phone ~ '^\+91[6-9][0-9]{9}$'),
    -- Rule: role is one of Customer, Operator, Admin.
    CONSTRAINT ck_users_role CHECK (role IN ('Customer', 'Operator', 'Admin')),
    -- Rule: status is one of PendingVerification, PendingApproval, Active, Rejected, Suspended.
    CONSTRAINT ck_users_status CHECK (
        status IN ('PendingVerification', 'PendingApproval', 'Active', 'Rejected', 'Suspended')),
    -- Rule: failed password attempts in the current window are 0 to 5.
    CONSTRAINT ck_users_failed_sign_in_count CHECK (failed_sign_in_count BETWEEN 0 AND 5),
    CONSTRAINT ck_users_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_users_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "Email is unique across all accounts, ignoring case." The index also serves every
-- sign-in and registration lookup, which compare lower(email).
CREATE UNIQUE INDEX uq_users_email ON authentication.users (lower(email));

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('authentication.users', 'user_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE authentication.users IS 'data-model/authentication/user.md';

COMMIT;
