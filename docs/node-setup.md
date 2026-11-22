# Setting up a node (store / warehouse) with sync to HQ

The Node Service runs on the node's server PC (Windows Service). Its master data comes from HQ; its documents go to HQ.
Sync contract: `docs/sync-contract.md`.

## 1. At HQ

1. Master Data → Toko & Gudang: create the node (e.g. `TK02`, type Toko) — it gets locations DISPLAY and DAMAGED.
2. Pengaturan → Pengguna: give the cashiers a role for this node (or for all nodes).
3. Open the node → **Buat token sinkron**. Copy the token now; it is shown once (a new token revokes the old one,
   "Cabut" revokes it).

## 2. On the node's server PC

1. MySQL 8.4, an EMPTY database (e.g. `pos_tk02`) and a user for it.
2. `appsettings.Local.json` next to the service (git-ignored):

   ```json
   {
     "ConnectionStrings": { "Pos": "Server=localhost;Port=3306;Database=pos_tk02;User=pos_app;Password=..." },
     "Node": { "Code": "TK02" },
     "Sync": { "HqUrl": "https://hq.example.com" }
   }
   ```

3. Store the token (encrypted with DPAPI, machine scope; never in appsettings):
   `Pos.NodeService set-sync-token pos_xxxxxxxx`
   (development: `dotnet run --project src/node/Pos.NodeService -- set-sync-token pos_xxx`).
4. Fill the database from HQ: `Pos.NodeService init-from-hq`. It migrates, refuses a database that already holds
   master data, pulls every master row page by page and marks the node initialized. Until then the node refuses to
   open shifts or save documents (503 `node_not_initialized`).
5. Register each cashier PC: `Pos.NodeService add-device "Kasir PC 1"` prints the device id; put it in that PC's
   desktop settings (`device_id`).
6. Start the service. The sync worker pushes the outbox, pulls master data and sends a heartbeat every 30 seconds
   (`Sync:IntervalSeconds`); without internet it backs off up to 5 minutes and catches up by itself. Status: `/health`
   (`sync`) and the cashier status bar ("Sinkron: 2 menit lalu · 0 tertunda", red after `sync.alert_minutes`).

## Replacing an old `pos_dev` (local dev seed) with HQ data

The dev seed (`seed-dev`) creates its own ids for every master row, so they can never match HQ's: a seeded database
cannot be "joined" to HQ. Start over instead:

1. At HQ (`pos_hq_dev`): `php artisan pos:seed-dev` (node TK01, admin/admin123, sample products), then Toko & Gudang →
   TK01 → Buat token sinkron.
2. Stop the Node Service. Keep the old data if wanted (`mysqldump pos_dev > pos_dev_old.sql`), then empty it:
   `DROP DATABASE pos_dev; CREATE DATABASE pos_dev;` (or point `ConnectionStrings:Pos` at a new database).
3. `appsettings.Local.json`: `"Sync": { "HqUrl": "http://127.0.0.1:8000" }` (where `php artisan serve` runs).
4. `dotnet run --project src/node/Pos.NodeService -- set-sync-token pos_...`, then `-- init-from-hq`, then
   `-- add-device "Kasir PC 1"` and put the id in the desktop settings.
5. Log in with an HQ user (e.g. admin/admin123; cashiers made at HQ). Sales from now on reach HQ.

Documents sold in the old `pos_dev` stay there; they belong to node ids HQ does not know and are not sent.
