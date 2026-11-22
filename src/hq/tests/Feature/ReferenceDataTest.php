<?php

use App\Models\User;
use App\Support\MasterData\MasterDataWriter;
use App\Support\MasterData\WriteContext;
use Illuminate\Support\Facades\DB;
use Inertia\Testing\AssertableInertia as Assert;

/*
| Data pendukung (12c): Metode Bayar (including the system method RETURN), Alasan and Jenis Biaya, all through the
| master data writer.
*/

function supportAdmin(): User
{
    return editor(['hq.access', 'settings.manage']);
}

it('creates and updates payment methods; never cash and credit together', function () {
    $this->actingAs(supportAdmin());
    $code = uid('QR');

    $this->post('/pengaturan/payment-methods', ['code' => strtolower($code), 'name' => 'QRIS BCA', 'account_code' => '1-1200'])->assertSessionHasNoErrors();
    $row = DB::table('payment_methods')->where('code', $code)->first();
    expect($row->name)->toBe('QRIS BCA')->and((bool) $row->is_cash)->toBeFalse()->and((bool) $row->is_active)->toBeTrue()
        ->and(lastAudit('payment_methods', $row->id)->action)->toBe('CREATE');

    $this->put("/pengaturan/payment-methods/{$row->id}", ['code' => $code, 'name' => 'QRIS', 'is_active' => false])->assertSessionHasNoErrors();
    expect((bool) DB::table('payment_methods')->where('id', $row->id)->value('is_active'))->toBeFalse()
        ->and(versionOf('payment_methods', $row->id))->toBeGreaterThan((int) $row->version);

    $this->post('/pengaturan/payment-methods', ['code' => uid('X'), 'name' => 'X', 'is_cash' => true, 'is_credit' => true])
        ->assertSessionHasErrors(['is_credit' => 'Metode bayar tidak bisa tunai dan kredit sekaligus.']);
    $this->post('/pengaturan/payment-methods', ['code' => $code, 'name' => 'Lagi'])->assertSessionHasErrors('code');
});

it('keeps the RETURN method fixed except for its name', function () {
    $this->actingAs(supportAdmin());

    $this->post('/pengaturan/payment-methods/return')->assertSessionHasNoErrors();
    $this->post('/pengaturan/payment-methods/return')->assertSessionHasNoErrors();      // already there: nothing new
    expect(DB::table('payment_methods')->where('code', 'RETURN')->count())->toBe(1);
    $id = (string) DB::table('payment_methods')->where('code', 'RETURN')->value('id');

    $this->put("/pengaturan/payment-methods/{$id}", ['code' => 'LAIN', 'name' => 'Retur Barang', 'is_cash' => true, 'is_active' => false])
        ->assertSessionHasNoErrors();
    $row = DB::table('payment_methods')->where('id', $id)->first();
    expect($row->code)->toBe('RETURN')->and($row->name)->toBe('Retur Barang')
        ->and((bool) $row->is_cash)->toBeFalse()->and((bool) $row->is_credit)->toBeFalse()->and((bool) $row->is_active)->toBeTrue();

    $other = app(MasterDataWriter::class)->create('payment_methods', ['code' => uid('M'), 'name' => 'Lain'], WriteContext::system());
    $this->put("/pengaturan/payment-methods/{$other}", ['code' => 'RETURN', 'name' => 'X'])->assertSessionHasErrors('code');

    $this->get('/pengaturan/payment-methods')->assertOk()
        ->assertInertia(fn (Assert $page) => $page->component('settings/payment-methods')->where('returnCode', 'RETURN'));
});

it('manages reasons per type with names unique among active rows', function () {
    $this->actingAs(supportAdmin());
    $name = 'Salah input '.uid();

    $this->post('/pengaturan/reasons', ['type' => 'SALE_VOID', 'name' => $name])->assertSessionHasNoErrors();
    $id = (string) DB::table('reasons')->where('name', $name)->value('id');
    $this->post('/pengaturan/reasons', ['type' => 'SALE_VOID', 'name' => $name])
        ->assertSessionHasErrors(['name' => 'Alasan ini sudah ada untuk jenis tersebut.']);
    $this->post('/pengaturan/reasons', ['type' => 'RETURN', 'name' => $name])->assertSessionHasNoErrors();   // other type: fine
    $this->post('/pengaturan/reasons', ['type' => 'LAINNYA', 'name' => 'X'])->assertSessionHasErrors('type');

    $this->put("/pengaturan/reasons/{$id}", ['type' => 'ADJUSTMENT', 'name' => $name])->assertSessionHasNoErrors();
    expect(DB::table('reasons')->where('id', $id)->value('type'))->toBe('ADJUSTMENT');

    $this->delete("/pengaturan/reasons/{$id}")->assertSessionHasNoErrors();
    expect(DB::table('reasons')->where('id', $id)->value('deleted_at'))->not->toBeNull()
        ->and(lastAudit('reasons', $id)->action)->toBe('DELETE');
    $this->post('/pengaturan/reasons', ['type' => 'ADJUSTMENT', 'name' => $name])->assertSessionHasNoErrors();  // deleted: name free again
});

it('manages expense types', function () {
    $this->actingAs(supportAdmin());
    $name = 'Listrik '.uid();

    $this->post('/pengaturan/expense-types', ['name' => $name, 'account_code' => '6-1100'])->assertSessionHasNoErrors();
    $id = (string) DB::table('expense_types')->where('name', $name)->value('id');
    expect(lastAudit('expense_types', $id)->action)->toBe('CREATE');
    $this->post('/pengaturan/expense-types', ['name' => $name])->assertSessionHasErrors(['name' => 'Jenis biaya ini sudah ada.']);

    $this->put("/pengaturan/expense-types/{$id}", ['name' => $name, 'account_code' => ''])->assertSessionHasNoErrors();
    expect(DB::table('expense_types')->where('id', $id)->value('account_code'))->toBeNull();

    $this->delete("/pengaturan/expense-types/{$id}")->assertSessionHasNoErrors();
    expect(DB::table('expense_types')->where('id', $id)->value('deleted_at'))->not->toBeNull();
    $this->get('/pengaturan/expense-types')->assertOk()->assertInertia(fn (Assert $page) => $page->component('settings/expense-types'));
});
