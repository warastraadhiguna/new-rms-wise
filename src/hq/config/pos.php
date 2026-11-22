<?php

return [
    /*
    | The SQL migrations shared with the Node Service (database/migrations, the ONLY source of the domain schema).
    | Run with `php artisan pos:migrate`; journaled in schema_versions exactly like DbUp at the nodes.
    */
    'migrations_path' => env('POS_MIGRATIONS_PATH', base_path('../../database/migrations')),

    /* Code of the HQ node: user_roles at this node (or with node_id NULL) give the permissions at HQ. */
    'hq_node_code' => env('POS_HQ_NODE_CODE', 'HQ'),

    /* Times are stored in UTC; screens show them in the branch timezone, else this one. */
    'display_timezone' => env('POS_DISPLAY_TIMEZONE', 'Asia/Jakarta'),

    /* Sync API for the nodes (docs/sync-contract.md). */
    'sync' => [
        'pull_default_limit' => 500,
        'pull_max_limit' => 1000,
        'push_max_documents' => 200,
        'push_max_rows_per_document' => 5000,
        'max_body_bytes' => (int) env('POS_SYNC_MAX_BODY_BYTES', 10 * 1024 * 1024),
        'rate_per_minute' => (int) env('POS_SYNC_RATE_PER_MINUTE', 120),
    ],
];
