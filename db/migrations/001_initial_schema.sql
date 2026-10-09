-- BrewForge - initial schema (PostgreSQL 16)
-- Generated from the Report 3 data dictionary. Do not hand-edit:
-- change schema_def.py and regenerate, so the document and the
-- database never drift apart.

BEGIN;

-- Role  (A role group with its permission set.)
CREATE TABLE role (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    role_name              VARCHAR(40) NOT NULL,
    permissions            JSONB NOT NULL DEFAULT '[]',
    PRIMARY KEY (id),
    UNIQUE (role_name),
    CHECK (role_name IN ('ADMIN', 'RD_SPECIALIST', 'RD_MANAGER', 'TRAINER', 'TRAINEE', 'QUALITY_AUDITOR', 'BRANCH_MANAGER', 'TRAINING_MANAGER'))
);

-- User  (A system user holding exactly one role and, for store-level roles, one branch.)
CREATE TABLE app_user (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    username               VARCHAR(64) NOT NULL,
    email                  VARCHAR(160) NOT NULL,
    password_hash          VARCHAR(255) NOT NULL,
    full_name              VARCHAR(120) NOT NULL,
    role_id                BIGINT NOT NULL,
    branch_id              BIGINT,
    status                 VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (username),
    UNIQUE (email),
    CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

-- Branch  (A physical outlet of the chain.)
CREATE TABLE branch (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_code            VARCHAR(16) NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    address                VARCHAR(255),
    status                 VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    PRIMARY KEY (id),
    UNIQUE (branch_code),
    CHECK (status IN ('ACTIVE', 'CLOSED'))
);

-- StandardEquipment  (An equipment class of the chain-wide standard profile, with the dosing thresholds the validator checks against.)
CREATE TABLE standard_equipment (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    equipment_code         VARCHAR(24) NOT NULL,
    equipment_class        VARCHAR(40) NOT NULL,
    min_threshold          NUMERIC(10,3) NOT NULL,
    max_threshold          NUMERIC(10,3) NOT NULL,
    dosing_unit            VARCHAR(12) NOT NULL,
    status                 VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    PRIMARY KEY (id),
    UNIQUE (equipment_code),
    UNIQUE (equipment_class),
    CHECK (min_threshold <= max_threshold),
    CHECK (dosing_unit IN ('g', 'ml', 'sec', 'degC', 'bar'))
);

-- Ingredient  (A raw material with its unit, shelf-life rule and storage rule.)
CREATE TABLE ingredient (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    ingredient_code        VARCHAR(24) NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    unit                   VARCHAR(12) NOT NULL,
    shelf_life_hours       INTEGER NOT NULL,
    storage_rule           VARCHAR(255),
    status                 VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    PRIMARY KEY (id),
    UNIQUE (ingredient_code),
    CHECK (shelf_life_hours > 0),
    CHECK (unit IN ('g', 'ml', 'pcs'))
);

-- Recipe  (The logical beverage. Owns many versions but has at most one in RELEASED state (BR-02).)
CREATE TABLE recipe (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_code            VARCHAR(24) NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    category               VARCHAR(40) NOT NULL,
    origin                 VARCHAR(16) NOT NULL DEFAULT 'NEW',
    created_by             BIGINT NOT NULL,
    status                 VARCHAR(16) NOT NULL DEFAULT 'ACTIVE',
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (recipe_code),
    CHECK (category IN ('TEA', 'COFFEE', 'OTHER')),
    CHECK (origin IN ('NEW', 'EXISTING'))
);

-- RecipeVersion  (A numbered revision of a recipe. Once released it is immutable (BR-01).)
CREATE TABLE recipe_version (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_id              BIGINT NOT NULL,
    version_no             INTEGER NOT NULL,
    state                  VARCHAR(16) NOT NULL DEFAULT 'DRAFT',
    is_immutable           BOOLEAN NOT NULL DEFAULT false,
    content_hash           CHAR(64),
    created_by             BIGINT NOT NULL,
    approved_by            BIGINT,
    released_at            TIMESTAMPTZ,
    superseded_at          TIMESTAMPTZ,
    PRIMARY KEY (id),
    UNIQUE (recipe_id, version_no),
    CHECK (state IN ('DRAFT', 'VALIDATED', 'REJECTED', 'RELEASED', 'SUPERSEDED')),
    CHECK (approved_by IS NULL OR approved_by <> created_by)
);

-- RecipeStep  (An ordered step of a recipe version.)
CREATE TABLE recipe_step (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_version_id      BIGINT NOT NULL,
    step_order             INTEGER NOT NULL,
    action_text            VARCHAR(500) NOT NULL,
    equipment_class        VARCHAR(40),
    technique_gate         VARCHAR(255),
    duration_seconds       INTEGER,
    PRIMARY KEY (id),
    UNIQUE (recipe_version_id, step_order),
    CHECK (step_order >= 1)
);

-- StepDependency  (A directed edge of the step dependency graph. The graph must be acyclic (BR-09).)
CREATE TABLE step_dependency (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    step_id                BIGINT NOT NULL,
    depends_on_step_id     BIGINT NOT NULL,
    dependency_type        VARCHAR(24) NOT NULL DEFAULT 'FINISH_TO_START',
    PRIMARY KEY (id),
    UNIQUE (step_id, depends_on_step_id),
    CHECK (step_id <> depends_on_step_id),
    CHECK (dependency_type IN ('FINISH_TO_START', 'REQUIRES_OUTPUT'))
);

-- StepIngredient  (An ingredient consumed by a step, with the quantity the validator checks.)
CREATE TABLE step_ingredient (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    step_id                BIGINT NOT NULL,
    ingredient_id          BIGINT NOT NULL,
    quantity               NUMERIC(10,3) NOT NULL,
    unit                   VARCHAR(12) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (step_id, ingredient_id),
    CHECK (quantity > 0)
);

-- ValidationResult  (The outcome of one validator check for one version, retained for audit.)
CREATE TABLE validation_result (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_version_id      BIGINT NOT NULL,
    check_type             VARCHAR(24) NOT NULL,
    passed                 BOOLEAN NOT NULL,
    step_id                BIGINT,
    violation_detail       JSONB,
    run_at                 TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CHECK (check_type IN ('EQUIPMENT', 'ORDERING', 'INGREDIENT'))
);

-- AiDraftLog  (The prompt, model and raw response of every LLM call, so a released recipe can be traced to its generation (BR-06).)
CREATE TABLE ai_draft_log (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_id              BIGINT NOT NULL,
    prompt_text            TEXT NOT NULL,
    model_name             VARCHAR(64) NOT NULL,
    raw_response           JSONB,
    schema_valid           BOOLEAN NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- AuditLog  (Append-only log of every state-changing action. No interface may update or delete a row.)
CREATE TABLE audit_log (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id                BIGINT,
    entity_type            VARCHAR(48) NOT NULL,
    entity_id              BIGINT NOT NULL,
    action                 VARCHAR(32) NOT NULL,
    payload_json           JSONB,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- TrainingRegulation  (The policy layer of the training process, held as data so the system can enforce it.)
CREATE TABLE training_regulation (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_type            VARCHAR(24) NOT NULL,
    mandatory_for_role     VARCHAR(40),
    prerequisite_type      VARCHAR(24),
    due_days               INTEGER NOT NULL,
    max_retakes            INTEGER NOT NULL DEFAULT 2,
    min_attendance_pct     INTEGER NOT NULL DEFAULT 80,
    effective_from         DATE NOT NULL,
    created_by             BIGINT NOT NULL,
    PRIMARY KEY (id),
    CHECK (course_type IN ('INDUCTION', 'PRODUCT', 'EQUIPMENT', 'RECERTIFICATION')),
    CHECK (due_days > 0),
    CHECK (max_retakes >= 0),
    CHECK (min_attendance_pct BETWEEN 0 AND 100)
);

-- Course  (A training course. A PRODUCT course is bound to exactly one released recipe version (BR-18).)
CREATE TABLE course (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_version_id      BIGINT,
    course_type            VARCHAR(24) NOT NULL,
    title                  VARCHAR(160) NOT NULL,
    total_duration_min     INTEGER,
    created_by             BIGINT NOT NULL,
    approved_by            BIGINT,
    state                  VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    published_at           TIMESTAMPTZ,
    PRIMARY KEY (id),
    CHECK (course_type IN ('INDUCTION', 'PRODUCT', 'EQUIPMENT', 'RECERTIFICATION')),
    CHECK (state IN ('DRAFT', 'PENDING_APPROVAL', 'PUBLISHED', 'OUT_OF_DATE', 'ARCHIVED')),
    CHECK (course_type <> 'PRODUCT' OR recipe_version_id IS NOT NULL)
);

-- CourseModule  (One of the seven modules of a course (BR-29).)
CREATE TABLE course_module (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_id              BIGINT NOT NULL,
    module_type            VARCHAR(32) NOT NULL,
    module_order           INTEGER NOT NULL,
    source                 VARCHAR(12) NOT NULL,
    duration_minutes       INTEGER,
    state                  VARCHAR(16) NOT NULL DEFAULT 'EMPTY',
    PRIMARY KEY (id),
    UNIQUE (course_id, module_type),
    UNIQUE (course_id, module_order),
    CHECK (module_order BETWEEN 1 AND 7),
    CHECK (module_type IN ('PRODUCT_OVERVIEW', 'INGREDIENTS', 'EQUIPMENT', 'SOP', 'TECHNIQUE', 'COMMON_MISTAKES', 'EXCEPTION_HANDLING')),
    CHECK (source IN ('GENERATED', 'AUTHORED', 'MIXED')),
    CHECK (state IN ('EMPTY', 'DRAFT', 'COMPLETE', 'NEEDS_REVIEW'))
);

-- Lesson  (One teaching unit inside a module. A SOP lesson keeps the link to the recipe step it came from.)
CREATE TABLE lesson (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_module_id       BIGINT NOT NULL,
    lesson_order           INTEGER NOT NULL,
    title                  VARCHAR(160) NOT NULL,
    content                TEXT,
    media_url              VARCHAR(500),
    recipe_step_id         BIGINT,
    PRIMARY KEY (id),
    UNIQUE (course_module_id, lesson_order)
);

-- Quiz  (The single theoretical assessment of a course.)
CREATE TABLE quiz (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_id              BIGINT NOT NULL,
    title                  VARCHAR(160) NOT NULL,
    pass_score             INTEGER NOT NULL DEFAULT 80,
    question_count         INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (id),
    UNIQUE (course_id),
    CHECK (pass_score BETWEEN 0 AND 100)
);

-- QuizQuestion  (A multiple-choice question, tagged to the module it tests (BR-35).)
CREATE TABLE quiz_question (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    quiz_id                BIGINT NOT NULL,
    course_module_id       BIGINT NOT NULL,
    question_text          VARCHAR(500) NOT NULL,
    options_json           JSONB NOT NULL,
    correct_option         VARCHAR(8) NOT NULL,
    PRIMARY KEY (id)
);

-- TrainingClass  (A scheduled run of one course for a group of trainees at a branch.)
CREATE TABLE training_class (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_id              BIGINT NOT NULL,
    branch_id              BIGINT NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    start_date             DATE NOT NULL,
    end_date               DATE NOT NULL,
    opened_by              BIGINT NOT NULL,
    state                  VARCHAR(16) NOT NULL DEFAULT 'PLANNED',
    PRIMARY KEY (id),
    CHECK (end_date >= start_date),
    CHECK (state IN ('PLANNED', 'RUNNING', 'CLOSED', 'CANCELLED'))
);

-- TrainingSession  (One teaching session of a class.)
CREATE TABLE training_session (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    training_class_id      BIGINT NOT NULL,
    session_no             INTEGER NOT NULL,
    scheduled_date         DATE NOT NULL,
    start_time             TIME NOT NULL,
    duration_minutes       INTEGER NOT NULL,
    location               VARCHAR(120),
    trainer_id             BIGINT NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (training_class_id, session_no),
    CHECK (duration_minutes > 0)
);

-- SessionModule  (The modules covered by one session.)
CREATE TABLE session_module (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    session_id             BIGINT NOT NULL,
    course_module_id       BIGINT NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (session_id, course_module_id)
);

-- Enrollment  (A trainee's registration on a course, with the deadline from the training regulation.)
CREATE TABLE enrollment (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    course_id              BIGINT NOT NULL,
    training_class_id      BIGINT,
    user_id                BIGINT NOT NULL,
    due_date               DATE,
    progress_percent       INTEGER NOT NULL DEFAULT 0,
    state                  VARCHAR(16) NOT NULL DEFAULT 'ASSIGNED',
    enrolled_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at           TIMESTAMPTZ,
    PRIMARY KEY (id),
    CHECK (progress_percent BETWEEN 0 AND 100),
    CHECK (state IN ('ASSIGNED', 'IN_PROGRESS', 'ELIGIBLE', 'PASSED', 'LOCKED', 'CLOSED'))
);

-- ModuleProgress  (A trainee's completion of one module. All seven are required before assessment (BR-32).)
CREATE TABLE module_progress (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    course_module_id       BIGINT NOT NULL,
    completed_at           TIMESTAMPTZ,
    PRIMARY KEY (id),
    UNIQUE (enrollment_id, course_module_id)
);

-- Attendance  (Whether one enrolled trainee attended one session.)
CREATE TABLE attendance (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    session_id             BIGINT NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    status                 VARCHAR(12) NOT NULL,
    note                   VARCHAR(255),
    recorded_by            BIGINT NOT NULL,
    recorded_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (session_id, enrollment_id),
    CHECK (status IN ('PRESENT', 'ABSENT', 'EXCUSED'))
);

-- QuizAttempt  (One attempt at the course quiz. The attempt number is capped by the regulation (BR-33).)
CREATE TABLE quiz_attempt (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    quiz_id                BIGINT NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    attempt_no             INTEGER NOT NULL,
    score                  INTEGER NOT NULL,
    passed                 BOOLEAN NOT NULL,
    answers_json           JSONB NOT NULL,
    attempted_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (enrollment_id, attempt_no),
    CHECK (score BETWEEN 0 AND 100)
);

-- PracticalEvaluation  (A trainer's structured observation of a trainee performing the procedure.)
CREATE TABLE practical_evaluation (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    evaluated_by           BIGINT NOT NULL,
    checklist_json         JSONB NOT NULL,
    passed                 BOOLEAN NOT NULL,
    evaluated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- Certificate  (The completion certificate, bound to the recipe version the course was built from (BR-13).)
CREATE TABLE certificate (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id                BIGINT NOT NULL,
    course_id              BIGINT NOT NULL,
    recipe_version_id      BIGINT,
    issued_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    status                 VARCHAR(16) NOT NULL DEFAULT 'VALID',
    superseded_by          BIGINT,
    PRIMARY KEY (id),
    UNIQUE (user_id, course_id, recipe_version_id),
    CHECK (status IN ('VALID', 'NEEDS_RECERT', 'SUPERSEDED'))
);

-- PilotProgram  (A time-boxed market test of one released version at a chosen set of branches.)
CREATE TABLE pilot_program (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_version_id      BIGINT NOT NULL,
    name                   VARCHAR(120) NOT NULL,
    start_date             DATE NOT NULL,
    end_date               DATE NOT NULL,
    criteria_json          JSONB NOT NULL,
    min_certified_staff    INTEGER NOT NULL DEFAULT 2,
    state                  VARCHAR(16) NOT NULL DEFAULT 'DRAFT',
    created_by             BIGINT NOT NULL,
    PRIMARY KEY (id),
    CHECK (end_date >= start_date),
    CHECK (state IN ('DRAFT', 'RUNNING', 'ENDED', 'CANCELLED'))
);

-- PilotBranch  (The participation of one branch in a pilot.)
CREATE TABLE pilot_branch (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    pilot_program_id       BIGINT NOT NULL,
    branch_id              BIGINT NOT NULL,
    readiness_state        VARCHAR(12) NOT NULL DEFAULT 'PREPARING',
    went_live_at           TIMESTAMPTZ,
    PRIMARY KEY (id),
    UNIQUE (pilot_program_id, branch_id),
    CHECK (readiness_state IN ('PREPARING', 'READY', 'LIVE'))
);

-- SalesRecord  (Cups of one drink sold at one branch on one trading day, attached to the version live there that day (BR-24).)
CREATE TABLE sales_record (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_id              BIGINT NOT NULL,
    recipe_id              BIGINT NOT NULL,
    recipe_version_id      BIGINT,
    trading_date           DATE NOT NULL,
    cups_sold              INTEGER NOT NULL,
    source                 VARCHAR(16) NOT NULL,
    recorded_by            BIGINT NOT NULL,
    recorded_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (branch_id, recipe_id, trading_date)   /* enforces BR-25 */,
    CHECK (cups_sold >= 0),
    CHECK (source IN ('MANUAL', 'POS_IMPORT'))
);

-- LaunchDecision  (The decision taken at the end of a pilot, with the figures as evaluated (BR-27).)
CREATE TABLE launch_decision (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    pilot_program_id       BIGINT NOT NULL,
    decision               VARCHAR(16) NOT NULL,
    evaluated_json         JSONB NOT NULL,
    decided_by             BIGINT NOT NULL,
    decided_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (pilot_program_id),
    CHECK (decision IN ('ROLLOUT', 'REVISE', 'DISCONTINUE'))
);

-- BranchLaunchStatus  (Whether a drink is live at a branch, on which version, and whether its certificate coverage is met.)
CREATE TABLE branch_launch_status (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_id              BIGINT NOT NULL,
    recipe_id              BIGINT NOT NULL,
    recipe_version_id      BIGINT,
    status                 VARCHAR(16) NOT NULL DEFAULT 'PREPARING',
    min_certified_staff    INTEGER NOT NULL DEFAULT 2,
    certified_count        INTEGER NOT NULL DEFAULT 0,
    coverage_met           BOOLEAN NOT NULL DEFAULT false,
    live_since             TIMESTAMPTZ,
    PRIMARY KEY (id),
    UNIQUE (branch_id, recipe_id),
    CHECK (status IN ('PREPARING', 'READY', 'LIVE', 'WITHDRAWN'))
);

-- ChangeImpact  (One computed row of an impact analysis, linking a trigger to an affected entity.)
CREATE TABLE change_impact (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    analysis_run_id        UUID NOT NULL,
    trigger_entity         VARCHAR(48) NOT NULL,
    trigger_id             BIGINT NOT NULL,
    affected_entity        VARCHAR(48) NOT NULL,
    affected_id            BIGINT NOT NULL,
    impact_type            VARCHAR(32) NOT NULL,
    committed              BOOLEAN NOT NULL DEFAULT false,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id)
);

-- ---------------------------------------------------------------
-- Foreign keys
-- ---------------------------------------------------------------
ALTER TABLE app_user ADD CONSTRAINT fk_app_user_role_id FOREIGN KEY (role_id) REFERENCES role(id) ON DELETE RESTRICT;
ALTER TABLE app_user ADD CONSTRAINT fk_app_user_branch_id FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE SET NULL;
ALTER TABLE recipe ADD CONSTRAINT fk_recipe_created_by FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE recipe_version ADD CONSTRAINT fk_recipe_version_recipe_id FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE CASCADE;
ALTER TABLE recipe_version ADD CONSTRAINT fk_recipe_version_created_by FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE recipe_version ADD CONSTRAINT fk_recipe_version_approved_by FOREIGN KEY (approved_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE recipe_step ADD CONSTRAINT fk_recipe_step_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE CASCADE;
ALTER TABLE recipe_step ADD CONSTRAINT fk_recipe_step_equipment_class FOREIGN KEY (equipment_class) REFERENCES standard_equipment(equipment_class) ON DELETE RESTRICT;
ALTER TABLE step_dependency ADD CONSTRAINT fk_step_dependency_step_id FOREIGN KEY (step_id) REFERENCES recipe_step(id) ON DELETE CASCADE;
ALTER TABLE step_dependency ADD CONSTRAINT fk_step_dependency_depends_on_step_id FOREIGN KEY (depends_on_step_id) REFERENCES recipe_step(id) ON DELETE CASCADE;
ALTER TABLE step_ingredient ADD CONSTRAINT fk_step_ingredient_step_id FOREIGN KEY (step_id) REFERENCES recipe_step(id) ON DELETE CASCADE;
ALTER TABLE step_ingredient ADD CONSTRAINT fk_step_ingredient_ingredient_id FOREIGN KEY (ingredient_id) REFERENCES ingredient(id) ON DELETE RESTRICT;
ALTER TABLE validation_result ADD CONSTRAINT fk_validation_result_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE CASCADE;
ALTER TABLE validation_result ADD CONSTRAINT fk_validation_result_step_id FOREIGN KEY (step_id) REFERENCES recipe_step(id) ON DELETE SET NULL;
ALTER TABLE ai_draft_log ADD CONSTRAINT fk_ai_draft_log_recipe_id FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE CASCADE;
ALTER TABLE audit_log ADD CONSTRAINT fk_audit_log_user_id FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE SET NULL;
ALTER TABLE training_regulation ADD CONSTRAINT fk_training_regulation_created_by FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE course ADD CONSTRAINT fk_course_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT;
ALTER TABLE course ADD CONSTRAINT fk_course_created_by FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE course ADD CONSTRAINT fk_course_approved_by FOREIGN KEY (approved_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE course_module ADD CONSTRAINT fk_course_module_course_id FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE CASCADE;
ALTER TABLE lesson ADD CONSTRAINT fk_lesson_course_module_id FOREIGN KEY (course_module_id) REFERENCES course_module(id) ON DELETE CASCADE;
ALTER TABLE lesson ADD CONSTRAINT fk_lesson_recipe_step_id FOREIGN KEY (recipe_step_id) REFERENCES recipe_step(id) ON DELETE SET NULL;
ALTER TABLE quiz ADD CONSTRAINT fk_quiz_course_id FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE CASCADE;
ALTER TABLE quiz_question ADD CONSTRAINT fk_quiz_question_quiz_id FOREIGN KEY (quiz_id) REFERENCES quiz(id) ON DELETE CASCADE;
ALTER TABLE quiz_question ADD CONSTRAINT fk_quiz_question_course_module_id FOREIGN KEY (course_module_id) REFERENCES course_module(id) ON DELETE RESTRICT;
ALTER TABLE training_class ADD CONSTRAINT fk_training_class_course_id FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE RESTRICT;
ALTER TABLE training_class ADD CONSTRAINT fk_training_class_branch_id FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE training_class ADD CONSTRAINT fk_training_class_opened_by FOREIGN KEY (opened_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE training_session ADD CONSTRAINT fk_training_session_training_class_id FOREIGN KEY (training_class_id) REFERENCES training_class(id) ON DELETE CASCADE;
ALTER TABLE training_session ADD CONSTRAINT fk_training_session_trainer_id FOREIGN KEY (trainer_id) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE session_module ADD CONSTRAINT fk_session_module_session_id FOREIGN KEY (session_id) REFERENCES training_session(id) ON DELETE CASCADE;
ALTER TABLE session_module ADD CONSTRAINT fk_session_module_course_module_id FOREIGN KEY (course_module_id) REFERENCES course_module(id) ON DELETE CASCADE;
ALTER TABLE enrollment ADD CONSTRAINT fk_enrollment_course_id FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE RESTRICT;
ALTER TABLE enrollment ADD CONSTRAINT fk_enrollment_training_class_id FOREIGN KEY (training_class_id) REFERENCES training_class(id) ON DELETE SET NULL;
ALTER TABLE enrollment ADD CONSTRAINT fk_enrollment_user_id FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE module_progress ADD CONSTRAINT fk_module_progress_enrollment_id FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE;
ALTER TABLE module_progress ADD CONSTRAINT fk_module_progress_course_module_id FOREIGN KEY (course_module_id) REFERENCES course_module(id) ON DELETE CASCADE;
ALTER TABLE attendance ADD CONSTRAINT fk_attendance_session_id FOREIGN KEY (session_id) REFERENCES training_session(id) ON DELETE CASCADE;
ALTER TABLE attendance ADD CONSTRAINT fk_attendance_enrollment_id FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE;
ALTER TABLE attendance ADD CONSTRAINT fk_attendance_recorded_by FOREIGN KEY (recorded_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE quiz_attempt ADD CONSTRAINT fk_quiz_attempt_quiz_id FOREIGN KEY (quiz_id) REFERENCES quiz(id) ON DELETE CASCADE;
ALTER TABLE quiz_attempt ADD CONSTRAINT fk_quiz_attempt_enrollment_id FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE;
ALTER TABLE practical_evaluation ADD CONSTRAINT fk_practical_evaluation_enrollment_id FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE;
ALTER TABLE practical_evaluation ADD CONSTRAINT fk_practical_evaluation_evaluated_by FOREIGN KEY (evaluated_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE certificate ADD CONSTRAINT fk_certificate_user_id FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE certificate ADD CONSTRAINT fk_certificate_course_id FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE RESTRICT;
ALTER TABLE certificate ADD CONSTRAINT fk_certificate_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT;
ALTER TABLE certificate ADD CONSTRAINT fk_certificate_superseded_by FOREIGN KEY (superseded_by) REFERENCES certificate(id) ON DELETE SET NULL;
ALTER TABLE pilot_program ADD CONSTRAINT fk_pilot_program_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT;
ALTER TABLE pilot_program ADD CONSTRAINT fk_pilot_program_created_by FOREIGN KEY (created_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE pilot_branch ADD CONSTRAINT fk_pilot_branch_pilot_program_id FOREIGN KEY (pilot_program_id) REFERENCES pilot_program(id) ON DELETE CASCADE;
ALTER TABLE pilot_branch ADD CONSTRAINT fk_pilot_branch_branch_id FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE sales_record ADD CONSTRAINT fk_sales_record_branch_id FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE sales_record ADD CONSTRAINT fk_sales_record_recipe_id FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE RESTRICT;
ALTER TABLE sales_record ADD CONSTRAINT fk_sales_record_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT;
ALTER TABLE sales_record ADD CONSTRAINT fk_sales_record_recorded_by FOREIGN KEY (recorded_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE launch_decision ADD CONSTRAINT fk_launch_decision_pilot_program_id FOREIGN KEY (pilot_program_id) REFERENCES pilot_program(id) ON DELETE CASCADE;
ALTER TABLE launch_decision ADD CONSTRAINT fk_launch_decision_decided_by FOREIGN KEY (decided_by) REFERENCES app_user(id) ON DELETE RESTRICT;
ALTER TABLE branch_launch_status ADD CONSTRAINT fk_branch_launch_status_branch_id FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE branch_launch_status ADD CONSTRAINT fk_branch_launch_status_recipe_id FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE RESTRICT;
ALTER TABLE branch_launch_status ADD CONSTRAINT fk_branch_launch_status_recipe_version_id FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT;

-- ---------------------------------------------------------------
-- Indexes, including the partial unique indexes that enforce
-- BR-02, BR-25 and BR-28 in the database rather than in code
-- ---------------------------------------------------------------
CREATE INDEX ix_user_role ON app_user(role_id);
CREATE INDEX ix_user_branch ON app_user(branch_id);
-- enforces BR-02
CREATE UNIQUE INDEX ux_recipe_one_released ON recipe_version(recipe_id) WHERE state = 'RELEASED';
CREATE INDEX ix_step_version ON recipe_step(recipe_version_id);
CREATE INDEX ix_valres_version ON validation_result(recipe_version_id);
CREATE INDEX ix_aidraft_recipe ON ai_draft_log(recipe_id);
CREATE INDEX ix_audit_entity ON audit_log(entity_type, entity_id);
CREATE INDEX ix_audit_time ON audit_log(created_at);
CREATE UNIQUE INDEX ux_course_one_per_version ON course(recipe_version_id) WHERE course_type = 'PRODUCT' AND state <> 'ARCHIVED';
CREATE INDEX ix_qq_module ON quiz_question(course_module_id);
CREATE UNIQUE INDEX ux_enrollment_active ON enrollment(course_id, user_id) WHERE state <> 'CLOSED';
CREATE INDEX ix_enrollment_user ON enrollment(user_id);
CREATE INDEX ix_praceval_enrol ON practical_evaluation(enrollment_id);
CREATE INDEX ix_cert_version ON certificate(recipe_version_id);
CREATE INDEX ix_cert_user ON certificate(user_id);
-- enforces BR-28
CREATE UNIQUE INDEX ux_pilot_active_version ON pilot_program(recipe_version_id) WHERE state IN ('DRAFT','RUNNING');
CREATE INDEX ix_sales_date ON sales_record(trading_date);
CREATE INDEX ix_sales_version ON sales_record(recipe_version_id);
CREATE INDEX ix_bls_recipe ON branch_launch_status(recipe_id);
CREATE INDEX ix_impact_run ON change_impact(analysis_run_id);

-- ---------------------------------------------------------------
-- audit_log is append-only: no interface may update or delete it
-- ---------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit_log_is_append_only()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only (BR: immutable audit trail)';
END $$;

CREATE TRIGGER trg_audit_log_no_update BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_is_append_only();

-- ---------------------------------------------------------------
-- A released recipe version is immutable (BR-01)
-- ---------------------------------------------------------------
CREATE OR REPLACE FUNCTION recipe_version_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.is_immutable AND (
           NEW.recipe_id     IS DISTINCT FROM OLD.recipe_id
        OR NEW.version_no    IS DISTINCT FROM OLD.version_no
        OR NEW.content_hash  IS DISTINCT FROM OLD.content_hash
        OR NEW.created_by    IS DISTINCT FROM OLD.created_by
        OR NEW.approved_by   IS DISTINCT FROM OLD.approved_by
        OR NEW.released_at   IS DISTINCT FROM OLD.released_at) THEN
        RAISE EXCEPTION 'recipe_version % is released and immutable (BR-01)',
                        OLD.id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_recipe_version_immutable BEFORE UPDATE ON recipe_version
    FOR EACH ROW EXECUTE FUNCTION recipe_version_immutable();

-- The steps of a released version are frozen too
CREATE OR REPLACE FUNCTION child_of_released_version_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_immutable BOOLEAN;
BEGIN
    SELECT rv.is_immutable INTO v_immutable
    FROM recipe_version rv
    WHERE rv.id = COALESCE(NEW.recipe_version_id, OLD.recipe_version_id);
    IF v_immutable THEN
        RAISE EXCEPTION 'the content of a released recipe version cannot '
                        'change (BR-01)';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

CREATE TRIGGER trg_recipe_step_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON recipe_step
    FOR EACH ROW EXECUTE FUNCTION child_of_released_version_immutable();

COMMIT;
