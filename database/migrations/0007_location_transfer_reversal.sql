-- =====================================================================
-- 0007: "Buat Mutasi Balik". A transfer is never edited or deleted; a reversal is a new transfer in the
-- opposite direction that points at the original. The unique key makes sure a transfer is reversed once
-- (NULLs do not collide).
-- =====================================================================

ALTER TABLE location_transfers
  ADD COLUMN reverses_id CHAR(36) CHARACTER SET ascii NULL AFTER notes,
  ADD UNIQUE KEY uk_location_transfers_reverses (reverses_id),
  ADD CONSTRAINT fk_lt_reverses FOREIGN KEY (reverses_id) REFERENCES location_transfers (id);
