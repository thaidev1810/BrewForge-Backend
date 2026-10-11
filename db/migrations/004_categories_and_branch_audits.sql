-- BrewForge - migration 004: categories and branch quality audits.
--
-- Not part of the developer pack. The role/page analysis asks for (a) the
-- categories an Administrator maintains (drink, ingredient, training,
-- equipment) and (b) the Quality Auditor's branch audit against the released
-- SOP, with the corrective action that follows a failed one.
--
-- Safe to run again, like every script after 001.

-- Category  (A label an administrator maintains. Deactivated, never deleted.)
CREATE TABLE IF NOT EXISTS category (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    kind                   VARCHAR(12) NOT NULL,
    code                   VARCHAR(40) NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    status                 VARCHAR(12) NOT NULL DEFAULT 'ACTIVE',
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (kind, code),
    CHECK (kind IN ('DRINK', 'INGREDIENT', 'TRAINING', 'EQUIPMENT')),
    CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

INSERT INTO category (kind, code, name) VALUES
    ('DRINK', 'MILK_TEA', 'Milk Tea'),
    ('DRINK', 'TEA', 'Tea'),
    ('DRINK', 'COFFEE', 'Coffee'),
    ('DRINK', 'FRUIT_TEA', 'Fruit Tea'),
    ('DRINK', 'SEASONAL', 'Seasonal'),
    ('INGREDIENT', 'TEA_LEAF', 'Tea leaf'),
    ('INGREDIENT', 'DAIRY', 'Dairy'),
    ('INGREDIENT', 'SYRUP', 'Syrup'),
    ('INGREDIENT', 'TOPPING', 'Topping'),
    ('TRAINING', 'ONBOARDING', 'Onboarding'),
    ('TRAINING', 'PRODUCT', 'Product'),
    ('TRAINING', 'RECERTIFICATION', 'Recertification'),
    ('EQUIPMENT', 'DISPENSER', 'Dispenser'),
    ('EQUIPMENT', 'BLENDER', 'Blender'),
    ('EQUIPMENT', 'SEALER', 'Sealer')
ON CONFLICT (kind, code) DO NOTHING;

-- BranchAudit  (One visit of a quality auditor to a branch, for one released version. Never changed or deleted.)
CREATE TABLE IF NOT EXISTS branch_audit (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_id              BIGINT NOT NULL,
    recipe_version_id      BIGINT NOT NULL,
    auditor_id             BIGINT NOT NULL,
    audited_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    score                  INTEGER NOT NULL,
    result                 VARCHAR(8) NOT NULL,
    comment                VARCHAR(1000),
    PRIMARY KEY (id),
    CHECK (score BETWEEN 0 AND 100),
    CHECK (result IN ('PASSED', 'FAILED')),
    FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT,
    FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT,
    FOREIGN KEY (auditor_id) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_branch_audit_branch ON branch_audit(branch_id, audited_at);

-- BranchAuditItem  (One line of the audit checklist.)
CREATE TABLE IF NOT EXISTS branch_audit_item (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_audit_id        BIGINT NOT NULL,
    criterion              VARCHAR(160) NOT NULL,
    expected               VARCHAR(255),
    actual                 VARCHAR(255),
    passed                 BOOLEAN NOT NULL,
    critical               BOOLEAN NOT NULL DEFAULT FALSE,
    comment                VARCHAR(500),
    evidence_url           VARCHAR(500),
    PRIMARY KEY (id),
    FOREIGN KEY (branch_audit_id) REFERENCES branch_audit(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_branch_audit_item_audit ON branch_audit_item(branch_audit_id);

-- CorrectiveAction  (What a failed audit obliges the branch to do. OPEN, then IN_PROGRESS, then CLOSED.)
CREATE TABLE IF NOT EXISTS corrective_action (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_audit_id        BIGINT NOT NULL,
    branch_id              BIGINT NOT NULL,
    description            VARCHAR(1000) NOT NULL,
    retraining_required    BOOLEAN NOT NULL DEFAULT FALSE,
    employee_ids           BIGINT[] NOT NULL DEFAULT '{}',
    status                 VARCHAR(12) NOT NULL DEFAULT 'OPEN',
    due_date               DATE,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at              TIMESTAMPTZ,
    closed_by              BIGINT,
    closure_note           VARCHAR(500),
    PRIMARY KEY (id),
    UNIQUE (branch_audit_id),
    CHECK (status IN ('OPEN', 'IN_PROGRESS', 'CLOSED')),
    CHECK ((status = 'CLOSED') = (closed_at IS NOT NULL)),
    FOREIGN KEY (branch_audit_id) REFERENCES branch_audit(id) ON DELETE RESTRICT,
    FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT,
    FOREIGN KEY (closed_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_corrective_action_branch ON corrective_action(branch_id, status);
