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
| 2026-11-20 | All rupiah rounding goes through `Pos.Shared.Money.Rupiah`. Storage precision is 4 dp; unit prices keep 4 dp. Sale line gross is rounded to the currency precision before discounts; discount and line total are at currency precision, with `line_total = gross - discount`. Midpoints round away from zero. |
| 2026-11-20 | Cash rounding modes are signed: `up` = toward +infinity, `down` = toward -infinity (also for refunds), so a mode always favours the same party. `nearest` rounds an exact half step away from zero. |
| 2026-11-20 | Sale line discount: percent first on the gross, then a nominal amount per line; capped so the line total is never negative. |

## Setting keys

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `rounding.cash_step` | decimal string | `"0"` | Cash totals are rounded to a multiple of this (e.g. `"100"`, `"500"`). `0` = no rounding. |
| `rounding.cash_mode` | string | `"nearest"` | `up`, `down` or `nearest` (see `CashRoundingModes`). |
| `currency.decimals` | integer 0..4 | `0` | Currency precision for line discounts and line totals. |

Full design document (Indonesian): https://claude.ai/artifact/4axAUbpgSNk1q77tM7PLsC
