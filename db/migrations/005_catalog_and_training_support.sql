-- BrewForge - migration 005: products, question bank, practical schedule, settings.
--
-- Not part of the developer pack. Follows the role/page analysis: the product
-- a recipe is sold as, a question bank the trainer keeps apart from any one
-- quiz, the appointment of a practical, and the settings an administrator
-- maintains.
--
-- Safe to run again, like every script after 001.

-- Product  (A drink as it is sold. Points at the recipe whose released version is the current SOP.)
CREATE TABLE IF NOT EXISTS product (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    product_code           VARCHAR(24) NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    category_id            BIGINT,
    recipe_id              BIGINT,
    description            VARCHAR(1000),
    image_url              VARCHAR(500),
    status                 VARCHAR(12) NOT NULL DEFAULT 'ACTIVE',
    created_by             BIGINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (product_code),
    CHECK (status IN ('ACTIVE', 'INACTIVE')),
    FOREIGN KEY (category_id) REFERENCES category(id) ON DELETE RESTRICT,
    FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE RESTRICT,
    FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

-- QuestionBankItem  (A question kept for reuse. Copied into a quiz, never shared with it.)
CREATE TABLE IF NOT EXISTS question_bank_item (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    question_type          VARCHAR(16) NOT NULL,
    question_text          VARCHAR(500) NOT NULL,
    options_json           JSONB NOT NULL,
    correct_options        VARCHAR(64) NOT NULL,
    course_id              BIGINT,
    module_type            VARCHAR(24),
    created_by             BIGINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CHECK (question_type IN ('SINGLE_CHOICE', 'MULTIPLE_CHOICE', 'TRUE_FALSE')),
    FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE SET NULL,
    FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_question_bank_course ON question_bank_item(course_id);

-- PracticalSchedule  (The appointment of a practical assessment.)
CREATE TABLE IF NOT EXISTS practical_schedule (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    trainer_id             BIGINT NOT NULL,
    scheduled_at           TIMESTAMPTZ NOT NULL,
    location               VARCHAR(160) NOT NULL,
    note                   VARCHAR(500),
    status                 VARCHAR(12) NOT NULL DEFAULT 'SCHEDULED',
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CHECK (status IN ('SCHEDULED', 'COMPLETED', 'CANCELLED')),
    FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE RESTRICT,
    FOREIGN KEY (trainer_id) REFERENCES app_user(id) ON DELETE RESTRICT
);

-- One open appointment per enrolment.
CREATE UNIQUE INDEX IF NOT EXISTS ux_practical_schedule_open ON practical_schedule(enrollment_id) WHERE status = 'SCHEDULED';
CREATE INDEX IF NOT EXISTS ix_practical_schedule_time ON practical_schedule(scheduled_at);

-- SystemSetting  (A named value an administrator maintains.)
CREATE TABLE IF NOT EXISTS system_setting (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    setting_key            VARCHAR(64) NOT NULL,
    value                  VARCHAR(500) NOT NULL,
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_by             BIGINT,
    PRIMARY KEY (id),
    UNIQUE (setting_key),
    FOREIGN KEY (updated_by) REFERENCES app_user(id) ON DELETE SET NULL
);

INSERT INTO system_setting (setting_key, value) VALUES
    ('company.name', 'BrewForge'),
    ('audit.pass_mark', '90')
ON CONFLICT (setting_key) DO NOTHING;
