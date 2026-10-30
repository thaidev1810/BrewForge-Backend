-- BrewForge - migration 019: periodic stock, for ingredient loss control.
--
-- A branch records what it receives (stock_receipt) and counts what it has on hand from time to time
-- (stock_count). Between two counts, the stock actually used is: opening count + received - closing
-- count. Compared with what the recipes say the sales should have used, the gap is the loss (over-
-- pouring, remakes, spoilage, theft). Quantities are stored in the ingredient's own unit.
--
-- Safe to run again, like every script after 001.

CREATE TABLE IF NOT EXISTS stock_receipt (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_id              BIGINT NOT NULL,
    ingredient_id          BIGINT NOT NULL,
    quantity               NUMERIC(14,3) NOT NULL,
    received_on            DATE NOT NULL,
    recorded_by            BIGINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CHECK (quantity > 0)
);

CREATE TABLE IF NOT EXISTS stock_count (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    branch_id              BIGINT NOT NULL,
    ingredient_id          BIGINT NOT NULL,
    quantity               NUMERIC(14,3) NOT NULL,
    counted_on             DATE NOT NULL,
    recorded_by            BIGINT NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    CHECK (quantity >= 0)
);

ALTER TABLE stock_receipt DROP CONSTRAINT IF EXISTS fk_stock_receipt_branch_id;
ALTER TABLE stock_receipt ADD CONSTRAINT fk_stock_receipt_branch_id
    FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE stock_receipt DROP CONSTRAINT IF EXISTS fk_stock_receipt_ingredient_id;
ALTER TABLE stock_receipt ADD CONSTRAINT fk_stock_receipt_ingredient_id
    FOREIGN KEY (ingredient_id) REFERENCES ingredient(id) ON DELETE RESTRICT;
ALTER TABLE stock_receipt DROP CONSTRAINT IF EXISTS fk_stock_receipt_recorded_by;
ALTER TABLE stock_receipt ADD CONSTRAINT fk_stock_receipt_recorded_by
    FOREIGN KEY (recorded_by) REFERENCES app_user(id) ON DELETE RESTRICT;

ALTER TABLE stock_count DROP CONSTRAINT IF EXISTS fk_stock_count_branch_id;
ALTER TABLE stock_count ADD CONSTRAINT fk_stock_count_branch_id
    FOREIGN KEY (branch_id) REFERENCES branch(id) ON DELETE RESTRICT;
ALTER TABLE stock_count DROP CONSTRAINT IF EXISTS fk_stock_count_ingredient_id;
ALTER TABLE stock_count ADD CONSTRAINT fk_stock_count_ingredient_id
    FOREIGN KEY (ingredient_id) REFERENCES ingredient(id) ON DELETE RESTRICT;
ALTER TABLE stock_count DROP CONSTRAINT IF EXISTS fk_stock_count_recorded_by;
ALTER TABLE stock_count ADD CONSTRAINT fk_stock_count_recorded_by
    FOREIGN KEY (recorded_by) REFERENCES app_user(id) ON DELETE RESTRICT;

CREATE INDEX IF NOT EXISTS ix_stock_receipt_branch_ingredient ON stock_receipt(branch_id, ingredient_id, received_on);
CREATE INDEX IF NOT EXISTS ix_stock_count_branch_ingredient ON stock_count(branch_id, ingredient_id, counted_on);
