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
];
