-- =============================================================================
-- V0009__create_operator_onboarding_operator_applications.sql
-- The operator_onboarding schema, and each time a bus company submitted its details for review.
-- =============================================================================
-- Implements: data-model/operator-onboarding/operator-application.md
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: uuid, keyed by the entity's identity attribute application_id.

BEGIN;

CREATE SCHEMA IF NOT EXISTS operator_onboarding;

CREATE TABLE operator_onboarding.operator_applications (
    application_id uuid        NOT NULL DEFAULT gen_random_uuid(),
    operator_id    uuid        NOT NULL,
    company_name   text        NOT NULL,
    contact_email  text        NOT NULL,
    gstin          text        NOT NULL,
    status         text        NOT NULL,
    submitted_at   timestamptz NOT NULL,
    reviewed_at    timestamptz,
    reviewed_by    uuid,
    reject_reason  text,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_operator_applications PRIMARY KEY (application_id),
    -- Relationship: belongs to one Operator User.
    CONSTRAINT fk_operator_applications_operator_id
        FOREIGN KEY (operator_id) REFERENCES authentication.users (user_id),
    -- Relationship: names the admin User who reviewed it.
    CONSTRAINT fk_operator_applications_reviewed_by
        FOREIGN KEY (reviewed_by) REFERENCES authentication.users (user_id),
    -- Rule: company name is 2-150 characters.
    CONSTRAINT ck_operator_applications_company_name_length CHECK (char_length(company_name) BETWEEN 2 AND 150),
    -- Rule: contact email is at most 254 characters.
    CONSTRAINT ck_operator_applications_contact_email_length CHECK (char_length(contact_email) <= 254),
    -- Rule: GSTIN is the 15-character standard format, upper case.
    CONSTRAINT ck_operator_applications_gstin_format
        CHECK (gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'),
    -- Rule: status is one of PendingVerification, PendingApproval, Approved, Rejected.
    CONSTRAINT ck_operator_applications_status
        CHECK (status IN ('PendingVerification', 'PendingApproval', 'Approved', 'Rejected')),
    -- Rule: a decision records the reviewing admin and time; only a decided application has them.
    CONSTRAINT ck_operator_applications_review_complete CHECK (
        (status IN ('Approved', 'Rejected') AND reviewed_at IS NOT NULL AND reviewed_by IS NOT NULL)
        OR (status IN ('PendingVerification', 'PendingApproval') AND reviewed_at IS NULL AND reviewed_by IS NULL)),
    -- Rule: a reject reason is 10-500 characters, and present exactly when the status is Rejected.
    CONSTRAINT ck_operator_applications_reject_reason CHECK (
        (status = 'Rejected' AND char_length(reject_reason) BETWEEN 10 AND 500)
        OR (status <> 'Rejected' AND reject_reason IS NULL)),
    CONSTRAINT ck_operator_applications_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_operator_applications_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "An operator has at most one application in PendingVerification or PendingApproval."
CREATE UNIQUE INDEX uq_operator_applications_undecided
    ON operator_onboarding.operator_applications (operator_id)
    WHERE status IN ('PendingVerification', 'PendingApproval');

-- Rule: "A GSTIN appears on at most one application in PendingVerification, PendingApproval or
-- Approved, across all operators." A rejected application does not hold its GSTIN.
CREATE UNIQUE INDEX uq_operator_applications_gstin_held
    ON operator_onboarding.operator_applications (gstin)
    WHERE status IN ('PendingVerification', 'PendingApproval', 'Approved');

-- An operator's applications, latest first: the review list, approve/reject and re-application
-- all start from the operator's latest application.
CREATE INDEX ix_operator_applications_operator_id
    ON operator_onboarding.operator_applications (operator_id, submitted_at DESC);

-- The review queue: latest applications by status, oldest submission first.
CREATE INDEX ix_operator_applications_status_submitted_at
    ON operator_onboarding.operator_applications (status, submitted_at);

CREATE INDEX ix_operator_applications_reviewed_by ON operator_onboarding.operator_applications (reviewed_by);

-- Attach the audit columns (backstop) + the stamp/capture trigger. Idempotent.
SELECT audit.fn_enable_audit('operator_onboarding.operator_applications', 'application_id');

-- Traceability: the in-database pointer back to the logical model (checked by CI).
COMMENT ON TABLE operator_onboarding.operator_applications IS 'data-model/operator-onboarding/operator-application.md';

COMMIT;
