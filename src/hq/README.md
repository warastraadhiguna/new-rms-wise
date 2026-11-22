# POS HQ (Laravel 13 + Inertia + React + TypeScript)

Web pusat: master data, pembelian, monitoring, akuntansi. Aturan proyek: lihat `CLAUDE.md` di root repo (bagian HQ).

```bash
composer install && npm install
cp .env.example .env && php artisan key:generate   # isi DB_* (lokal: pos_hq_dev, pos_app)
php artisan pos:migrate        # skema domain dari ../../database/migrations/*.sql (journal schema_versions, sama dengan DbUp)
php artisan migrate            # hanya tabel framework (sessions, cache, jobs)
npm run build                  # atau: composer dev
php artisan test               # Pest, memakai database pos_hq_test
```
