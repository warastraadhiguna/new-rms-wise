-- =====================================================================
-- 0006: Form Mutasi Lokasi (location transfer inside one node).
--  location_transfer_lines keeps one row per expiry date taken FEFO from the source (base_qty), as
--  before. These columns keep what the admin typed, so the transfer detail can show "2 BOX":
--  * line_no:          the entered line the expiry row belongs to (1, 2, ...)
--  * product_unit_id:  the unit chosen on that line
--  * qty:              the quantity typed in that unit (same value on every expiry row of the line)
--  Listing by period uses (node_id, transferred_at).
-- =====================================================================

ALTER TABLE location_transfer_lines
  ADD COLUMN line_no         SMALLINT      NOT NULL DEFAULT 1 AFTER location_transfer_id,
  ADD COLUMN product_unit_id CHAR(36) CHARACTER SET ascii NULL AFTER product_id,
  ADD COLUMN qty             DECIMAL(18,4) NULL AFTER product_unit_id,
  ADD CONSTRAINT fk_ltl_product_unit FOREIGN KEY (product_unit_id) REFERENCES product_units (id);

ALTER TABLE location_transfers
  ADD KEY ix_location_transfers_date (node_id, transferred_at);
