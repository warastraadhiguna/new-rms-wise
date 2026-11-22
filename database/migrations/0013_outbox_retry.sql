-- Node sync agent: a document HQ asked to send again later (e.g. a master row HQ does not know yet) waits until
-- next_attempt_at, so a few such documents never fill every batch and hold back the documents after them.
-- Pending = sent_at IS NULL AND failed_at IS NULL.
ALTER TABLE sync_outbox
  ADD COLUMN next_attempt_at DATETIME(3) NULL AFTER attempts,
  DROP INDEX ix_sync_outbox_pending,
  ADD KEY ix_sync_outbox_pending (sent_at, failed_at, id);
