-- BrewForge - migration 011: feedback on a recipe from the people who make the drink.
--
-- Not part of the developer pack. In the pack a recipe travels one way: R&D
-- writes it, staff are trained on it, the counter makes it. What the counter
-- learns making it a hundred times - where it goes wrong, what a customer
-- said - has no way back. A row here is one such remark, about one released
-- version, from a member of staff trained on the drink or from a customer as
-- recorded at a branch. R&D accepts or rejects it, and says which later
-- version took it in.
--
-- Safe to run again, like every script after 001.

-- RecipeFeedback  (One remark about a version of a drink, and what R&D made of it.)
CREATE TABLE IF NOT EXISTS recipe_feedback (
    id                       BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_id                BIGINT NOT NULL,
    recipe_version_id        BIGINT NOT NULL,
    recipe_step_id           BIGINT,
    branch_id                BIGINT,
    source                   VARCHAR(12) NOT NULL,
    category                 VARCHAR(20) NOT NULL,
    content                  VARCHAR(1000) NOT NULL,
    rating                   INTEGER,
    submitted_by             BIGINT NOT NULL,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT now(),
    state                    VARCHAR(12) NOT NULL DEFAULT 'NEW',
    reviewed_by              BIGINT,
    reviewed_at              TIMESTAMPTZ,
    review_note              VARCHAR(500),
    addressed_in_version_id  BIGINT,
    PRIMARY KEY (id),
    CHECK (source IN ('STAFF', 'CUSTOMER')),
    CHECK (category IN ('PREPARATION_NOTE', 'COMMON_MISTAKE', 'TASTE', 'OTHER')),
    CHECK (state IN ('NEW', 'ACCEPTED', 'REJECTED', 'ADDRESSED')),
    CHECK (rating IS NULL OR rating BETWEEN 1 AND 5),
    -- A customer's remark comes with a rating and from a branch; a rating is a customer's.
    CHECK ((source = 'CUSTOMER') = (rating IS NOT NULL)),
    CHECK (source <> 'CUSTOMER' OR branch_id IS NOT NULL),
    -- Reviewed exactly when it is no longer new.
    CHECK ((state = 'NEW') = (reviewed_by IS NULL)),
    CHECK ((state = 'ADDRESSED') = (addressed_in_version_id IS NOT NULL)),
    CHECK (addressed_in_version_id IS NULL OR addressed_in_version_id <> recipe_version_id),
    FOREIGN KEY (recipe_id) REFERENCES recipe(id) ON DELETE RESTRICT,
    FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT,
    FOREIGN KEY (recipe_step_id) REFERENCES recipe_step(id) ON DELETE RESTRICT,
    FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT,
    FOREIGN KEY (submitted_by) REFERENCES app_user(id) ON DELETE RESTRICT,
    FOREIGN KEY (reviewed_by) REFERENCES app_user(id) ON DELETE RESTRICT,
    FOREIGN KEY (addressed_in_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_recipefeedback_recipe ON recipe_feedback(recipe_id, state);
CREATE INDEX IF NOT EXISTS ix_recipefeedback_submitter ON recipe_feedback(submitted_by);
