-- Unique keys and nullable columns. MySQL treats every NULL as distinct, so a unique key containing a nullable
-- column does not stop duplicates where that column is NULL (e.g. two global rows of the same setting).
-- Audit of every UNIQUE KEY / nullable column (0001-0010):
--   settings (branch_id, setting_key)      branch_id NULL = global  -> locked below (deleted rows included: they are
--                                           restored, never re-inserted)
--   user_roles                             had no unique key at all; node_id NULL = all nodes -> one ACTIVE row per
--                                           user, role and node (revoked rows stay as history)
--   prices                                 had no unique key; branch_id NULL = every branch -> one ACTIVE price per
--                                           unit, branch, min_qty and effective_from (two would be ambiguous)
--   promotions                             no natural key (several promotions may overlap on purpose): nothing to lock
--   goods_receipts (delivery_note_id), purchase_invoices (supplier_id, supplier_invoice_no),
--   location_transfers (reverses_id), sale_payments (sale_return_id)
--                                           NULL means "not applicable" (receipt without a delivery note, invoice
--                                           without a supplier number, not a reversal, not paid by a return):
--                                           many NULLs are intended; the key only guards real values.
-- The generated key columns are computed by MySQL: inserts and sync never write them.

ALTER TABLE settings
  ADD COLUMN branch_key CHAR(36) CHARACTER SET ascii AS (COALESCE(branch_id, '')) STORED AFTER branch_id,
  ADD KEY ix_settings_branch (branch_id),                -- fk_settings_branch used uk_settings
  DROP INDEX uk_settings,
  ADD UNIQUE KEY uk_settings (branch_key, setting_key);

ALTER TABLE user_roles
  ADD COLUMN node_key CHAR(36) CHARACTER SET ascii AS (COALESCE(node_id, '')) STORED AFTER node_id,
  ADD COLUMN active_key TINYINT AS (IF(deleted_at IS NULL, 1, NULL)) STORED AFTER deleted_at,
  ADD UNIQUE KEY uk_user_roles_active (user_id, role_id, node_key, active_key);

ALTER TABLE prices
  ADD COLUMN branch_key CHAR(36) CHARACTER SET ascii AS (COALESCE(branch_id, '')) STORED AFTER branch_id,
  ADD COLUMN active_key TINYINT AS (IF(deleted_at IS NULL, 1, NULL)) STORED AFTER deleted_at,
  ADD UNIQUE KEY uk_prices_active (product_unit_id, branch_key, min_qty, effective_from, active_key);
