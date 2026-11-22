<?php

use App\Console\Commands\PosSeedDev;
use Illuminate\Support\Facades\Artisan;
use Illuminate\Support\Facades\DB;

it('creates entity, branch, nodes HQ and TK01, role OWNER and admin/admin123, once', function () {
    expect(Artisan::call('pos:seed-dev'))->toBe(0);
    $versionAfterFirst = (int) DB::table('master_version')->value('last_version');
    expect(Artisan::call('pos:seed-dev'))->toBe(0)
        ->and(Artisan::output())->toContain('nothing to do')
        ->and((int) DB::table('master_version')->value('last_version'))->toBe($versionAfterFirst);

    expect(DB::table('nodes')->whereIn('code', ['HQ', 'TK01'])->orderBy('code')->pluck('type')->all())->toBe(['HQ', 'STORE'])
        ->and(DB::table('branches')->where('code', 'B01')->value('timezone'))->toBe('Asia/Jakarta')
        ->and(json_decode(DB::table('roles')->where('code', 'OWNER')->value('permissions'), true))->toBe(['*'])
        ->and(DB::table('users')->where('username', PosSeedDev::ADMIN_USERNAME)->value('password_hash'))->toStartWith('$2y$')
        ->and(DB::table('audit_logs')->where('table_name', 'nodes')->whereNull('user_id')->count())->toBeGreaterThanOrEqual(2);
    expect(DB::table('nodes')->where('code', 'HQ')->value('version'))->toBeGreaterThan(0);

    $this->post('/login', ['username' => 'admin', 'password' => 'admin123'])->assertRedirect('/dashboard');
});
