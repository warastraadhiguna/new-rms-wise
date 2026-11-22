-- =====================================================================
-- 0008: Tutup Shift (9d).
--  * closed_by: who closed the shift (the cashier, or a user with shift.close for another
--    cashier's shift; then notes are required).
--  * The summary is frozen at close, so the report and the synced row never change afterwards:
--    cash_sales, credit_down_payments, receivable_cash, cash_expenses, non_cash_totals
--    (JSON array of {code, name, amount, count}), sale_count, cancellation_count.
--    expected_cash / counted_cash / notes already exist (0001).
-- =====================================================================

ALTER TABLE cashier_shifts
  ADD COLUMN closed_by            CHAR(36) CHARACTER SET ascii NULL AFTER closed_at,
  ADD COLUMN cash_sales           DECIMAL(18,4) NULL AFTER opening_cash,
  ADD COLUMN credit_down_payments DECIMAL(18,4) NULL AFTER cash_sales,
  ADD COLUMN receivable_cash      DECIMAL(18,4) NULL AFTER credit_down_payments,
  ADD COLUMN cash_expenses        DECIMAL(18,4) NULL AFTER receivable_cash,
  ADD COLUMN non_cash_totals      JSON          NULL AFTER cash_expenses,
  ADD COLUMN sale_count           INT           NULL AFTER non_cash_totals,
  ADD COLUMN cancellation_count   INT           NULL AFTER sale_count,
  ADD CONSTRAINT fk_cs_closed_by FOREIGN KEY (closed_by) REFERENCES users (id);
