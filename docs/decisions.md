# Decisions log

| Date | Decision |
| --- | --- |
| 2026-11-19 | Offline-first: each store/warehouse is a node with its own MySQL + Node Service; HQ on Ubuntu. |
| 2026-11-19 | HQ: Laravel + MySQL. Node: ASP.NET Core Node Service (Windows Service) + WPF clients. Android: Kotlin. |
| 2026-11-19 | Clients go through the Node Service API; no direct MySQL access from client PCs. |
| 2026-11-19 | Physical-goods work is local; decisions, purchasing, money and accounting are at HQ. |
| 2026-11-19 | Warehouses are separate nodes; goods move between nodes with delivery notes (QR) and goods receipts. |
| 2026-11-19 | One entity now, schema ready for multiple entities; branch/node are reporting dimensions. |
| 2026-11-20 | All names in English. Schema designed fresh (v3), not mirrored from the old app. |
| 2026-11-20 | Old app stays for historical sales and old payables/receivables. Only products and opening stock are migrated. |
| 2026-11-20 | UUID v7 for synced tables, BIGINT auto-increment for local/HQ-internal tables. |
| 2026-11-20 | Cost of goods: moving average, computed at HQ. Negative stock allowed with later cost correction. |
| 2026-11-20 | All purchasing through HQ. |
| 2026-11-20 | Returns and credit payments at any store (option B, read-only replicas). Return window is a setting. |
| 2026-11-20 | Loyalty points and customer credit usable offline; HQ flags overuse after sync. |
| 2026-11-20 | Prices may differ per branch; setting to force uniform prices. |

Full design document (Indonesian): https://claude.ai/artifact/4axAUbpgSNk1q77tM7PLsC
