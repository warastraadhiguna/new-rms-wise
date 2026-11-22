# Sync contract (HQ ⇄ node), version 1

Base URL: `{hq}/api/v1/sync`. JSON (UTF-8). Every request carries `Authorization: Bearer <node token>`; the token
belongs to one node (Toko & Gudang → "Buat token sinkron", shown once, revocable) and every request acts as that node.
Request bodies may be sent with `Content-Encoding: gzip`; responses are gzip-compressed when the request says
`Accept-Encoding: gzip`. Errors: HTTP status + `{ "code": "snake_case", "message": "..." }`
(401 `invalid_token`, 403 `node_inactive`, 413 `batch_too_large`, 422 `invalid_request`, 429 rate limit).

Rules (CLAUDE.md): one owner per row; HQ owns master data, a node owns the documents with its `node_id`;
replicas are read-only; every node document has an outbox row; sync is idempotent.

## Values

Rows travel as they are stored: column name → value. `null`; strings; numbers for integers and booleans (0/1);
decimals as strings (`"12500.0000"`); `DATETIME(3)` as `"2026-11-22 03:15:00.123"` (UTC); `DATE` as `"2026-11-22"`;
JSON columns as their JSON text. Generated columns (e.g. `settings.branch_key`) are never sent or written.

## GET /pull?since={version}&limit={n}

Master data changed after `since` (HQ → node), ordered by `version` (one global sequence, see `MasterDataWriter`),
deleted rows included (`deleted_at` set = soft delete at the node too). `limit` default 500, max 1000.

```json
{ "since": 120, "next_since": 618, "safe_version": 900, "has_more": true,
  "changes": [ { "table": "products", "version": 121, "row": { "id": "…", "sku": "…", "version": 121, "deleted_at": null } } ] }
```

- Only versions `<= safe_version` are returned: the last committed `master_version`. Writers hold the
  `master_version` row lock from taking a version until commit, so every version up to the committed value is
  committed; a write still in flight is never skipped — it shows up in a later pull.
- Store the returned `next_since` and ask again while `has_more` is true. When nothing is left,
  `next_since = safe_version`.
- Filtered per node: `prices`, `settings` and `promotions` only with `branch_id` NULL or the node's branch.
- Tables (FK-safe apply order): entities, branches, nodes, locations, roles, users, user_roles, units, categories,
  brands, products, product_units, product_barcodes, prices, promotions, contacts, sales_reps, supplier_products,
  payment_methods, reasons, expense_types, settings. A row arrives with its latest version only, so a later page may
  hold a parent of an earlier page: the node applies a page in one transaction with foreign key checks off (HQ never
  hard-deletes, every parent arrives).

## POST /push

Outbox documents of this node (node → HQ), in outbox order. At most 200 documents and 10 MB (uncompressed) per
request.

```json
{ "documents": [ {
    "outbox_id": 41, "doc_type": "SALE", "doc_id": "0193…", "row_version": 1,
    "occurred_at": "2026-11-22 03:15:00.123", "app_version": "1.0.0",
    "payload": { "format": 2, "node_id": "…", "tables": [
      { "table": "sales", "rows": [ { … } ] }, { "table": "sale_lines", "rows": [ … ] } ] } } ] }
```

Response (always 200 when the request itself is valid; one bad document never fails the others):

```json
{ "results": [ { "outbox_id": 41, "doc_id": "0193…", "status": "accepted" },
               { "outbox_id": 42, "doc_id": "…", "status": "rejected", "code": "foreign_node", "message": "…", "retry": false } ] }
```

Status: `accepted` (stored), `duplicate` (same row_version already stored: nothing done), `stale` (HQ already has a
higher row_version: ignored), `rejected` (with `code`, `message` and `retry`: true = try again later, e.g. a master
row HQ does not know yet; false = never accepted, park it). The node marks `accepted`, `duplicate` and `stale` as sent.

Documents and their tables (`payload.tables`, in this order; each row must belong to the document):

| doc_type | tables |
| --- | --- |
| CASHIER_SHIFT (mutable, row_version) | devices (the shift's device), cashier_shifts |
| SALE | sales, sale_lines, sale_payments, stock_ledger, loyalty_point_ledger |
| SALE_VOID | sale_voids, stock_ledger, loyalty_point_ledger |
| SALE_RETURN | devices (if any), sale_returns, sale_return_lines, stock_ledger, loyalty_point_ledger |
| CART_CANCELLATION | devices, cart_cancellations, cart_cancellation_lines |
| LOCATION_TRANSFER | location_transfers, location_transfer_lines, stock_ledger |
| STOCK_ADJUSTMENT | stock_adjustments, stock_adjustment_lines, stock_ledger |

Checks: the root row has `id = doc_id` and `node_id` = the token's node; lines point to the root (`sale_id`,
`sale_return_id`, …); ledger rows have `doc_id = doc_id` and the node's `node_id`; a device row has the node's
`node_id` and is the root's `device_id`. A row may not overwrite a row of another node or another document.
HQ stores the copies in the same tables (upsert) and records `inbound_documents` (one row per doc_id, highest
row_version wins).

## POST /heartbeat

```json
{ "app_version": "1.0.0", "schema_version": "0012_sync.sql", "master_version": 618,
  "pending_outbox": 0, "failed_outbox": 0, "node_time": "2026-11-22 03:15:00.123" }
```

Response: `{ "hq_time": "…", "clock_skew_ms": 1250, "master_version": 900 }` (skew = node clock − HQ clock).
