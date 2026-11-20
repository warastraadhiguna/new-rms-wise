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
        "node_not_configured" => "Data toko belum tersedia di Node Service. Hubungi admin.",
        not null => $"Node Service menolak permintaan: {detail ?? code} ({code}).",
        null when status >= 500 => $"Node Service mengalami gangguan (HTTP {status}). Coba lagi atau hubungi admin.",
        null => $"Permintaan ditolak oleh Node Service (HTTP {status}).",
    };
}
