-- Tugas 13: synchronisation between the nodes and HQ.

-- HQ: sync tokens per node, created on the Toko & Gudang page and shown once. Only the SHA-256 hash is stored.
-- HQ-internal (not synced). Creating a new token revokes the node's previous ones.
CREATE TABLE node_sync_tokens (
  id            BIGINT        NOT NULL AUTO_INCREMENT,
  node_id       CHAR(36) CHARACTER SET ascii NOT NULL,
  token_hash    CHAR(64) CHARACTER SET ascii NOT NULL,   -- SHA-256 hex
  token_hint    VARCHAR(12)   NOT NULL,                  -- last characters, to recognise a token on screen
  created_by    CHAR(36) CHARACTER SET ascii NULL,
  created_at    DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  last_used_at  DATETIME(3)   NULL,
  revoked_at    DATETIME(3)   NULL,
  revoked_by    CHAR(36) CHARACTER SET ascii NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_node_sync_tokens_hash (token_hash),
  KEY ix_node_sync_tokens_node (node_id, revoked_at),
  CONSTRAINT fk_nst_node    FOREIGN KEY (node_id)    REFERENCES nodes (id),
  CONSTRAINT fk_nst_created FOREIGN KEY (created_by) REFERENCES users (id),
  CONSTRAINT fk_nst_revoked FOREIGN KEY (revoked_by) REFERENCES users (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Node: an outbox row HQ refused for good (e.g. a document of another node) is parked instead of retried forever.
ALTER TABLE sync_outbox
  ADD COLUMN failed_at DATETIME(3) NULL AFTER sent_at;

-- HQ: what the monitoring shows per node, besides the heartbeat figures.
ALTER TABLE node_heartbeats
  ADD COLUMN failed_outbox INT         NOT NULL DEFAULT 0 AFTER pending_outbox,
  ADD COLUMN node_time     DATETIME(3) NULL AFTER clock_skew_ms,
  ADD COLUMN last_push_at  DATETIME(3) NULL AFTER node_time,
  ADD COLUMN last_pull_at  DATETIME(3) NULL AFTER last_push_at,
  ADD COLUMN last_ip       VARCHAR(45) NULL AFTER last_pull_at;
