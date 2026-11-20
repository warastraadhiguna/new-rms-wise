-- =====================================================================
-- 0005: receipt printing (RMS 2 struk).
--  * sales.discount_pct: the sale discount as typed when the cashier chose "Diskon" (percent);
--    NULL when it was a "Potongan" (amount) or there was no discount. The receipt shows
--    "Diskon 10%" or "Potongan". discount_amount stays the amount actually deducted.
--  Receipt texts are settings (no schema): receipt.header_lines, receipt.footer_lines (JSON arrays).
-- =====================================================================

ALTER TABLE sales
  ADD COLUMN discount_pct DECIMAL(9,4) NULL AFTER discount_amount;
