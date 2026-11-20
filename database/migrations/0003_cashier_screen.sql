-- =====================================================================
-- 0003: cashier screen support.
--  * products.allow_qty_multiplier: 0 = every item must be scanned (qty per line at most 1),
--    as "isCanPengali" in the old application.
--  * sales.discount_approved_by: supervisor who approved a sale discount above the cashier's limit.
--  * approval_tokens (node only, never synced): one-time supervisor approvals for a single action.
-- =====================================================================

ALTER TABLE products
  ADD COLUMN allow_qty_multiplier TINYINT(1) NOT NULL DEFAULT 1 AFTER track_expiry;

ALTER TABLE sales
  ADD COLUMN discount_approved_by CHAR(36) CHARACTER SET ascii NULL AFTER discount_amount,
  ADD CONSTRAINT fk_sales_discount_approver FOREIGN KEY (discount_approved_by) REFERENCES users (id);

CREATE TABLE approval_tokens (
  id               BIGINT        NOT NULL AUTO_INCREMENT,
  token_hash       CHAR(64) CHARACTER SET ascii NOT NULL,   -- SHA-256 hex; the token itself is never stored
  action           VARCHAR(40)   NOT NULL,                  -- sale.line.delete | sale.discount.override
  approved_by      CHAR(36) CHARACTER SET ascii NOT NULL,   -- supervisor
  requested_by     CHAR(36) CHARACTER SET ascii NOT NULL,   -- user the approval was given to
  expires_at       DATETIME(3)   NOT NULL,
  used_at          DATETIME(3)   NULL,
  used_for_doc_id  CHAR(36) CHARACTER SET ascii NULL,
  created_at       DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  UNIQUE KEY uk_approval_tokens_hash (token_hash),
  CONSTRAINT fk_at_approved_by  FOREIGN KEY (approved_by)  REFERENCES users (id),
  CONSTRAINT fk_at_requested_by FOREIGN KEY (requested_by) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
