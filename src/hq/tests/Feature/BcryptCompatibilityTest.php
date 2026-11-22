<?php

use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Hash;

/*
| The node (C#, BCrypt.Net-Next 4.1.0 via PasswordHasher.Hash) and HQ (PHP password_hash / Laravel Hash) must read
| each other's hashes: users.password_hash is master data that HQ writes and the nodes verify.
| The other direction (a PHP hash verified in C#) is tested in src/node: PasswordHasherCompatibilityTests.
*/

const NODE_HASH = '$2y$12$F/GsJoKZTibiAO44hcaUJOzwFyHAC4dwmQLaWBUX9YfBHVAeijXdu';   // "rahasia-dari-node", made in C#

it('verifies a hash made by the node in C#', function () {
    expect(password_verify('rahasia-dari-node', NODE_HASH))->toBeTrue()
        ->and(Hash::check('rahasia-dari-node', NODE_HASH))->toBeTrue()
        ->and(Hash::check('rahasia-dari-hq', NODE_HASH))->toBeFalse();
});

it('makes $2y$ bcrypt hashes like the node, cost 12 by default', function () {
    $hash = Hash::make('admin123');

    expect($hash)->toStartWith('$2y$')
        ->and(password_get_info($hash)['algoName'])->toBe('bcrypt')
        ->and(config('hashing.driver'))->toBe('bcrypt');

    // phpunit.xml lowers the rounds for speed; the application default is 12 like PasswordHasher.DefaultWorkFactor.
    expect((string) file_get_contents(base_path('config/hashing.php')))->toContain("env('BCRYPT_ROUNDS', 12)");
});

it('logs in a user whose hash was made by the node', function () {
    $user = makeUser();
    DB::table('users')->where('id', $user->id)->update(['password_hash' => NODE_HASH]);

    $this->post('/login', ['username' => $user->username, 'password' => 'rahasia-dari-node'])->assertRedirect('/dashboard');
});
