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
| 2026-11-20 | Stock is split into expiry buckets per location + product. The null bucket means no/unknown expiry (`stock_balances.expiry_key = 9999-12-31`); `9999-12-31` is never accepted as a real expiry. Only the null bucket may go negative. Allocation logic is `Pos.Shared.Stock.StockAllocator`. |
| 2026-11-20 | Outbound (sale, delivery, transfer out) is FEFO: dated buckets with positive stock, earliest expiry first; same-date buckets are merged, so the result never depends on input order. Expired buckets (`expiry < today` in the branch timezone) are skipped unless `allowExpired`; expiring today is still sellable. The shortage goes to the null bucket. |
| 2026-11-20 | Inbound (receipt, return, transfer in) with expiry E: a negative null bucket is covered up to 0 first (goods sold while short are assumed to come from this delivery), the rest goes to bucket E. Inbound without expiry goes to the null bucket. Negative dated buckets are never covered. |
| 2026-11-20 | `track_expiry = false`: outbound first drains any remaining dated buckets FEFO (expired included), then the null bucket; inbound always goes to the null bucket. Lets a product switch the flag off without stranding dated stock. |
| 2026-11-20 | One document line produces one `stock_ledger` entry per expiry bucket. Ledger ids are UUID v7, `base_qty` is signed (+ in, - out), `occurred_at` must be UTC, `doc_type` must be one of `StockDocTypes`. |
| 2026-11-20 | Transfers keep the expiry of each FEFO allocation. Within a node: one `LOCATION_TRANSFER` with out entries at the source and in entries at the destination (`StockTransfer.Transfer`). Across nodes: the sender's delivery has one line per expiry; the receiver's goods receipt reads those expiries (`StockTransfer.AllocateReceipt`), covering a negative null bucket earliest expiry first. Total out == total in. |
| 2026-11-20 | "Today" for expiry checks is the branch-local date of the Node Service UTC clock (`BranchClock.Today`, IANA timezone from `branches.timezone`). |
| 2026-11-20 | Database server: MySQL 8.0.19+ only (upsert alias syntax); MariaDB is refused at startup. Every connection runs in UTC. Deadlocks (1213) and lock wait timeouts (1205) re-run the whole transaction up to 3 times. |
| 2026-11-20 | Node identity comes from setting `Node:Code`; the node row arrives with master data, so endpoints that need it answer 503 until it exists. |
| 2026-11-20 | Document numbers `{NODE}-{TYPE}-{YYYYMM}-{000001}` from `document_sequences`, month in the branch timezone. The sequence row is locked until the document commits, so numbers are unique and gapless (a rollback returns the number). Type codes: `SL` sale, `SH` cashier shift, `ADJ` stock adjustment. |
| 2026-11-20 | API and sync JSON: snake_case, decimals as strings with 4 decimals, times ISO-8601 UTC with milliseconds (`PosJson`). Each document writes its `sync_outbox` row (full payload) in the same transaction. |
| 2026-11-20 | Cashier shifts: one OPEN shift per device (device row locked while opening). Close computes `expected_cash = opening_cash + cash tendered - change given` over cash payment methods; sales hold a shared lock on their shift so a close waits for them. Shift is a mutable document: open = row_version 1, close = 2, each with an outbox row. Cashier/device come from the request until authentication exists. |
| 2026-11-20 | API errors are RFC 7807 problem details with a stable snake_case `code` (422 validation, 409 state conflict, 404 unknown, 503 node not configured). |
| 2026-11-20 | Dev data comes from `dotnet run --project src/node/Pos.NodeService -- seed-dev` (refuses databases not named `*_dev`), never from a migration. Opening stock is posted through a real ADJUSTMENT document. |

## Setting keys

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `rounding.cash_step` | decimal string | `"0"` | Cash totals are rounded to a multiple of this (e.g. `"100"`, `"500"`). `0` = no rounding. |
| `rounding.cash_mode` | string | `"nearest"` | `up`, `down` or `nearest` (see `CashRoundingModes`). |
| `currency.decimals` | integer 0..4 | `0` | Currency precision for line discounts and line totals. |

Full design document (Indonesian): https://claude.ai/artifact/4axAUbpgSNk1q77tM7PLsC
