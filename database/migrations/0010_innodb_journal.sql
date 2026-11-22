-- =====================================================================
-- 0010: every table InnoDB.
-- All domain tables already declare ENGINE=InnoDB. The migration journal schema_versions is created by
-- DbUp (and by HQ's pos:migrate with DbUp's DDL) without an engine, so on servers whose default engine is
-- MyISAM (WAMP) it became MyISAM. Convert it; the Node Service and HQ refuse to start when any table of the
-- database is not InnoDB.
-- =====================================================================

ALTER TABLE schema_versions ENGINE=InnoDB;
