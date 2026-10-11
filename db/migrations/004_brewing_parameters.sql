-- BrewForge - migration 004: brewing parameters.
--
-- Not part of the developer pack. The pack's schema records the temperature
-- and the pressure of a step only as free text in the technique gate, so a
-- kettle dosed in degC or a machine dosed in bar could not be range-checked
-- (BR-10), and nothing held the water for a tea to the temperature that tea
-- is brewed at. For a specialty tea chain that is the heart of the recipe.
--
-- Safe to run again, like every script after 001. Adding a column does not
-- fire the row triggers that keep a released version immutable (BR-01), and
-- the released rows keep NULL in the new columns: they say nothing they did
-- not say before.

-- The temperature the step is done at and the pressure it is done under, where the recipe states them.
ALTER TABLE recipe_step ADD COLUMN IF NOT EXISTS temperature_c NUMERIC(5,1);
ALTER TABLE recipe_step ADD COLUMN IF NOT EXISTS pressure_bar  NUMERIC(4,1);

-- The window a leaf is brewed in. Both bounds or neither.
ALTER TABLE ingredient ADD COLUMN IF NOT EXISTS brew_temp_min_c NUMERIC(5,1);
ALTER TABLE ingredient ADD COLUMN IF NOT EXISTS brew_temp_max_c NUMERIC(5,1);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'recipe_step_temperature_range') THEN
        ALTER TABLE recipe_step ADD CONSTRAINT recipe_step_temperature_range
            CHECK (temperature_c IS NULL OR temperature_c BETWEEN 0 AND 100);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'recipe_step_pressure_range') THEN
        ALTER TABLE recipe_step ADD CONSTRAINT recipe_step_pressure_range
            CHECK (pressure_bar IS NULL OR (pressure_bar > 0 AND pressure_bar <= 20));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ingredient_brew_window') THEN
        ALTER TABLE ingredient ADD CONSTRAINT ingredient_brew_window
            CHECK ((brew_temp_min_c IS NULL AND brew_temp_max_c IS NULL)
                   OR (brew_temp_min_c IS NOT NULL AND brew_temp_max_c IS NOT NULL
                       AND brew_temp_min_c >= 0 AND brew_temp_max_c <= 100 AND brew_temp_min_c <= brew_temp_max_c));
    END IF;
END $$;
