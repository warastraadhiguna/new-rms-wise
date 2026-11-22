namespace Pos.Desktop.Services;

/// <summary>A Node Service call failed. <c>Message</c> is Indonesian and ready to show to the cashier.</summary>
/// <param name="code">The server's problem code, or a client code such as <c>node_unreachable</c>.</param>
public sealed class NodeApiException(string message, string? code, int? statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>Indonesian texts for Node Service problem codes.</summary>
public static class ErrorMessages
{
    public static string Unreachable(string url) =>
        $"Node Service tidak dapat dihubungi di {url}. Pastikan komputer server toko menyala dan jaringan tersambung.";

    public const string DeviceNotConfigured =
        "ID perangkat belum diatur di file konfigurasi kasir. Hubungi admin toko.";

    public static string For(string? code, int status, string? detail, int? retryAfterSeconds) => code switch
    {
        "invalid_credentials" => "Username atau password salah.",
        "account_locked" => $"Terlalu banyak percobaan gagal. Coba lagi dalam {Math.Max(1, (int)Math.Ceiling((retryAfterSeconds ?? 300) / 60.0))} menit.",
        "no_access_to_node" => "Pengguna ini tidak punya akses ke toko ini.",
        "user_inactive" => "Pengguna ini sudah tidak aktif. Hubungi admin toko.",
        "token_expired" or "unauthorized" => "Sesi login sudah berakhir. Silakan login kembali.",
        "forbidden" => "Anda tidak punya izin untuk tindakan ini.",
        "device_required" or "device_not_allowed" => "Perangkat ini belum terdaftar atau belum aktif di toko ini. Hubungi admin.",
        "shift_already_open" => "Perangkat ini masih punya shift terbuka.",
        "invalid_amount" => "Jumlah uang tidak valid.",
        "approval_rejected" => "Username atau password supervisor salah.",
        "approval_not_permitted" => "Supervisor ini tidak berwenang menyetujui tindakan tersebut.",
        "no_open_shift" => "Belum ada shift terbuka di perangkat ini.",
        "not_shift_owner" => "Shift di perangkat ini milik kasir lain.",
        "reason_required" => "Keterangan wajib diisi.",
        "unknown_sale" => "Nota tidak ditemukan di Node Service.",
        "same_location" => "Lokasi asal dan tujuan tidak boleh sama.",
        "unknown_location" => "Lokasi tidak dikenal di toko/gudang ini.",
        "unknown_transfer" => "Mutasi tidak ditemukan.",
        "already_reversed" => "Mutasi ini sudah pernah dibalik; satu mutasi hanya bisa dibalik sekali.",
        "reversal_direction" => "Mutasi balik harus dari lokasi tujuan ke lokasi asal mutasi semula.",
        "unknown_shift" => "Shift tidak ditemukan di Node Service.",
        "shift_closed" => "Shift sudah ditutup, gunakan Retur [F12].",
        "exceeds_returnable" => "Jumlah retur melebihi sisa yang bisa diretur (terjual dikurangi yang sudah diretur).",
        "invalid_return_qty" => "Jumlah retur tidak valid.",
        "not_a_credit_sale" => "Potong Piutang hanya untuk nota kredit.",
        "exceeds_receivable" => "Nilai retur melebihi sisa piutang nota ini; pilih Uang Kembali.",
        "invalid_settlement" => "Penyelesaian retur tidak dikenal.",
        "return_approval_required" => "Nota sudah lewat batas hari retur; perlu persetujuan supervisor.",
        "sale_voided" => "Nota ini sudah di-void; tidak bisa diretur.",
        "no_damaged_location" => "Toko ini belum punya lokasi barang rusak (DAMAGED).",
        "foreign_sale" => "Retur nota dari toko lain belum tersedia.",
        "unknown_return" => "Retur tidak ditemukan.",
        "exchange_already_used" => "Nilai retur ini sudah dipakai untuk tukar barang.",
        "exchange_amount_mismatch" => "Pembayaran retur harus sebesar nilai retur.",
        "not_an_exchange" => "Retur ini bukan Tukar Barang.",
        "already_voided" => "Nota ini sudah di-void.",
        "has_returns" => "Nota ini sudah punya retur; tidak bisa di-void.",
        "unknown_reason" => "Alasan tidak dikenal.",
        "void_approval_required" => "Void perlu persetujuan supervisor.",
        "history_limit" => "Tanggal awal melewati batas yang dapat dilihat kasir (setting sales.cashier_history_days).",
        "invalid_paging" => "Permintaan halaman data tidak valid.",
        "shift_already_closed" => "Shift ini sudah ditutup dan tidak bisa diubah.",
        "shift_close_forbidden" => "Shift ini milik kasir lain; menutupnya perlu izin tutup shift (shift.close).",
        "notes_required" => "Keterangan wajib diisi untuk menutup shift milik kasir lain.",
        "invalid_range" => "Tanggal Sampai lebih awal dari tanggal Dari.",
        "qty_multiplier_not_allowed" => "Barang ini harus discan satu per satu (tidak boleh memakai pengali).",
        "no_price" => "Ada barang yang belum punya harga.",
        "insufficient_payment" => "Jumlah bayar kurang dari total.",
        "non_cash_overpayment" => "Pembayaran non-tunai tidak boleh melebihi total; kembalian hanya dari tunai.",
        "credit_requires_customer" => "Pembayaran kredit wajib memilih member/pelanggan.",
        "credit_not_allowed" => "Pelanggan ini tidak punya fasilitas kredit.",
        "discount_approval_required" => "Diskon melebihi batas Anda; perlu persetujuan supervisor.",
        "invalid_approval" => "Persetujuan supervisor tidak berlaku lagi. Ulangi persetujuan.",
        "invalid_discount" => "Diskon tidak valid.",
        "unknown_payment_method" => "Cara bayar tidak dikenal.",
        "node_not_configured" => "Data toko belum tersedia di Node Service. Hubungi admin.",
        not null => $"Node Service menolak permintaan: {detail ?? code} ({code}).",
        null when status >= 500 => $"Node Service mengalami gangguan (HTTP {status}). Coba lagi atau hubungi admin.",
        null => $"Permintaan ditolak oleh Node Service (HTTP {status}).",
    };
}
