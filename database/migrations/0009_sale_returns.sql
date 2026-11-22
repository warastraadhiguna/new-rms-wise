-- =====================================================================
-- 0009: Retur penjualan (10c).
--  * sale_returns: device_id (where it was made), approved_by (supervisor for a return after
--    returns.window_days), refund_amount (cash paid back from the shift's drawer for REFUND).
--    settlement: REFUND (UANG KEMBALI) | EXCHANGE (TUKAR BARANG) | REDUCE_RECEIVABLE (POTONG PIUTANG).
--  * sale_return_lines: line_no and line_total (net refund of the line). The expiry dates taken back
--    are on the ledger rows (doc_type SALE_RETURN, doc_line_id = return line); expiry_date stays NULL.
--  * sale_payments.sale_return_id: TUKAR BARANG pays a new sale with payment method RETURN; unique,
--    so the value of one return is used once.
--  * cashier_shifts.cash_refunds: frozen at shift close like the other figures.
-- =====================================================================

ALTER TABLE sale_returns
  ADD COLUMN device_id     CHAR(36) CHARACTER SET ascii NULL AFTER shift_id,
  ADD COLUMN refund_amount DECIMAL(18,4) NOT NULL DEFAULT 0 AFTER total,
  ADD COLUMN approved_by   CHAR(36) CHARACTER SET ascii NULL AFTER notes,
  ADD KEY ix_sale_returns_shift (shift_id),
  ADD KEY ix_sale_returns_returned_at (node_id, returned_at),
  ADD CONSTRAINT fk_sr_device   FOREIGN KEY (device_id)   REFERENCES devices (id),
  ADD CONSTRAINT fk_sr_approved FOREIGN KEY (approved_by) REFERENCES users (id);

ALTER TABLE sale_return_lines
  ADD COLUMN line_no    SMALLINT      NOT NULL DEFAULT 0 AFTER sale_return_id,
  ADD COLUMN line_total DECIMAL(18,4) NOT NULL DEFAULT 0 AFTER unit_price,
  ADD KEY ix_srl_sale_line (sale_line_id);

ALTER TABLE sale_payments
  ADD COLUMN sale_return_id CHAR(36) CHARACTER SET ascii NULL AFTER reference,
  ADD UNIQUE KEY uk_sale_payments_return (sale_return_id),
  ADD CONSTRAINT fk_sp_return FOREIGN KEY (sale_return_id) REFERENCES sale_returns (id);

ALTER TABLE cashier_shifts
  ADD COLUMN cash_refunds DECIMAL(18,4) NULL AFTER cash_expenses;
