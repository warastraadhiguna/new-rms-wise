# Database migrations

These SQL files are the only source of truth for the schema (MySQL 8.4 LTS, InnoDB, utf8mb4).

- Files run in order by number: `0001_initial_schema.sql`, `0002_...sql`.
- Never edit a file that may already have been applied anywhere. Add a new file instead.
- Node Service applies them at startup with DbUp (journal table `schema_versions`).
- HQ (Laravel) runs the same files.
- Local test: `mysql -u root -e "CREATE DATABASE pos_dev"` then `mysql -u root pos_dev < 0001_initial_schema.sql`.
