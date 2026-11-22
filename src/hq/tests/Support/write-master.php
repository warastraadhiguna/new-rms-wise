<?php

/*
| Child process for the parallel master data test: boots the app (DB from the environment, i.e. pos_hq_test) and
| creates one unit through the writer. Prints the new row's version.
*/

use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Contracts\Console\Kernel;
use Illuminate\Support\Facades\DB;

require __DIR__.'/../../vendor/autoload.php';
$app = require __DIR__.'/../../bootstrap/app.php';
$app->make(Kernel::class)->bootstrap();

$code = $argv[1];
$id = app(MasterDataWriter::class)->create('units', ['code' => $code, 'name' => 'Paralel '.$code], WriteContext::system());
echo DB::table('units')->where('id', $id)->value('version');
