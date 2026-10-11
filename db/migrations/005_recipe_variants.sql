-- BrewForge - migration 005: the variants of a recipe version.
--
-- Not part of the developer pack. A drink is sold in sizes and hot or iced,
-- and the pack's schema has one set of quantities per version. A variant is
-- that same procedure with its quantities scaled: one factor for the whole
-- drink, and a factor of its own for an ingredient that does not follow it
-- (the ice of a hot drink is 0).
--
-- A variant is content of its version: frozen with it on release (BR-01).
--
-- Safe to run again, like every script after 001.

-- RecipeVariant  (One way a version is served: a size, hot or iced.)
CREATE TABLE IF NOT EXISTS recipe_variant (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_version_id      BIGINT NOT NULL,
    variant_code           VARCHAR(24) NOT NULL,
    name                   VARCHAR(80) NOT NULL,
    scale                  NUMERIC(5,3) NOT NULL DEFAULT 1,
    PRIMARY KEY (id),
    UNIQUE (recipe_version_id, variant_code),
    CHECK (scale > 0 AND scale <= 5),
    FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE CASCADE
);

-- RecipeVariantIngredient  (An ingredient that does not follow the scale of its variant.)
CREATE TABLE IF NOT EXISTS recipe_variant_ingredient (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    recipe_variant_id      BIGINT NOT NULL,
    ingredient_id          BIGINT NOT NULL,
    scale                  NUMERIC(5,3) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (recipe_variant_id, ingredient_id),
    CHECK (scale >= 0 AND scale <= 5),
    FOREIGN KEY (recipe_variant_id) REFERENCES recipe_variant(id) ON DELETE CASCADE,
    FOREIGN KEY (ingredient_id) REFERENCES ingredient(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_variant_version ON recipe_variant(recipe_version_id);

-- The variants of a released version are frozen with its steps (BR-01). The
-- function of the steps reads recipe_version_id from the row, so it serves
-- recipe_variant as it is; an ingredient of a variant finds its version
-- through the variant.
DROP TRIGGER IF EXISTS trg_recipe_variant_immutable ON recipe_variant;
CREATE TRIGGER trg_recipe_variant_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON recipe_variant
    FOR EACH ROW EXECUTE FUNCTION child_of_released_version_immutable();

CREATE OR REPLACE FUNCTION variant_ingredient_of_released_version_immutable()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_immutable BOOLEAN;
BEGIN
    SELECT rv.is_immutable INTO v_immutable
    FROM recipe_variant va JOIN recipe_version rv ON rv.id = va.recipe_version_id
    WHERE va.id = COALESCE(NEW.recipe_variant_id, OLD.recipe_variant_id);
    IF v_immutable THEN
        RAISE EXCEPTION 'the content of a released recipe version cannot '
                        'change (BR-01)';
    END IF;
    RETURN COALESCE(NEW, OLD);
END $$;

DROP TRIGGER IF EXISTS trg_recipe_variant_ingredient_immutable ON recipe_variant_ingredient;
CREATE TRIGGER trg_recipe_variant_ingredient_immutable
    BEFORE INSERT OR UPDATE OR DELETE ON recipe_variant_ingredient
    FOR EACH ROW EXECUTE FUNCTION variant_ingredient_of_released_version_immutable();
