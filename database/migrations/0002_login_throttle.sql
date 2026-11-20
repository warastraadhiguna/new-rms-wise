-- =====================================================================
-- 0002: failed-login tracking at the node (local only, never synced).
-- Users are HQ master data and must not be updated by a node, so the
-- lockout state lives in its own table, keyed by the normalised login
-- name (also for names that do not exist, so lockouts reveal nothing).
-- =====================================================================

CREATE TABLE login_throttle (
  login_name      VARCHAR(50)   NOT NULL,              -- lower-case, trimmed username
  failed_count    INT           NOT NULL DEFAULT 0,    -- consecutive failures since last success/lock
  locked_until    DATETIME(3)   NULL,
  last_failed_at  DATETIME(3)   NULL,
  updated_at      DATETIME(3)   NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3),
  PRIMARY KEY (login_name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
