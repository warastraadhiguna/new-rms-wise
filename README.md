# POS Multi-Branch

Offline-first point of sale for multiple stores and warehouses, synced to an HQ server.

- Rules for contributors and for Claude: `CLAUDE.md`
- Decisions: `docs/decisions.md`
- Schema: `database/migrations/`
- C# solution: `src/node/Pos.slnx` (open in Visual Studio 2026)

## Quick start (developer PC)
1. MySQL 8.x running (WAMP is fine, MySQL not MariaDB).
2. `mysql -u root -e "CREATE DATABASE pos_dev"`
3. Open `src/node/Pos.slnx`, build, run tests.
