-- =====================================================================
-- 0004: sales screen (RMS 2 "Form Penjualan").
--  * products.shelf_location: "Letak" column on the cashier screen (master data, HQ).
--  * cart_cancellations (+ lines): a cashier cancels a whole unpaid cart with a reason
--    (RMS 2 FormKeteranganBatalTransaksi). Node document, owner = node_id, synced via outbox.
-- =====================================================================

ALTER TABLE products
  ADD COLUMN shelf_location VARCHAR(40) NULL AFTER allow_qty_multiplier;

CREATE TABLE cart_cancellations (
  id              CHAR(36) CHARACTER SET ascii NOT NULL,     -- UUID v7 from the client (idempotent)
  node_id         CHAR(36) CHARACTER SET ascii NOT NULL,
  number          VARCHAR(40)   NOT NULL,
  shift_id        CHAR(36) CHARACTER SET ascii NOT NULL,
  device_id       CHAR(36) CHARACTER SET ascii NOT NULL,
  cashier_id      CHAR(36) CHARACTER SET ascii NOT NULL,
  cancelled_at    DATETIME(3)   NOT NULL,
  reason          TEXT          NOT NULL,
  total_amount    DECIMAL(18,4) NOT NULL DEFAULT 0,          -- cart total as shown to the cashier
  created_at      DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  PRIMARY KEY (id),
  UNIQUE KEY uk_cart_cancellations_number (node_id, number),
  KEY ix_cart_cancellations_shift (shift_id),
  CONSTRAINT fk_cc_node    FOREIGN KEY (node_id)    REFERENCES nodes (id),
  CONSTRAINT fk_cc_shift   FOREIGN KEY (shift_id)   REFERENCES cashier_shifts (id),
  CONSTRAINT fk_cc_device  FOREIGN KEY (device_id)  REFERENCES devices (id),
  CONSTRAINT fk_cc_cashier FOREIGN KEY (cashier_id) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE cart_cancellation_lines (
  id                   CHAR(36) CHARACTER SET ascii NOT NULL,
  cart_cancellation_id CHAR(36) CHARACTER SET ascii NOT NULL,
  line_no              SMALLINT      NOT NULL,
  product_id           CHAR(36) CHARACTER SET ascii NOT NULL,
  product_unit_id      CHAR(36) CHARACTER SET ascii NOT NULL,
  qty                  DECIMAL(18,4) NOT NULL,                -- in the line's unit
  unit_price           DECIMAL(18,4) NOT NULL,                -- as shown in the cart
  line_total           DECIMAL(18,4) NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_cart_cancellation_lines (cart_cancellation_id, line_no),
  CONSTRAINT fk_ccl_cancellation FOREIGN KEY (cart_cancellation_id) REFERENCES cart_cancellations (id),
  CONSTRAINT fk_ccl_product      FOREIGN KEY (product_id)           REFERENCES products (id),
  CONSTRAINT fk_ccl_unit         FOREIGN KEY (product_unit_id)      REFERENCES product_units (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
