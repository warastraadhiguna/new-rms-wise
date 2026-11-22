using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Shared.Sync;

/// <summary>Values of <c>sync_outbox.doc_type</c>.</summary>
public static class SyncDocTypes
{
    public const string Sale = "SALE";
    public const string CashierShift = "CASHIER_SHIFT";
    public const string StockAdjustment = "STOCK_ADJUSTMENT";
    public const string CartCancellation = "CART_CANCELLATION";
    public const string LocationTransfer = "LOCATION_TRANSFER";
    public const string SaleVoid = "SALE_VOID";
    public const string SaleReturn = "SALE_RETURN";
}

/// <summary>
/// JSON conventions for the API and sync payloads: snake_case names, money/qty as decimal strings
/// with 4 decimals ("12500.0000"), times as ISO-8601 UTC ("2026-11-20T03:15:00.123Z").
/// </summary>
public static class PosJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions());

    /// <summary>Applies the conventions to existing options (e.g. ASP.NET Core's).</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.Converters.Add(new DecimalStringConverter());
        options.Converters.Add(new UtcDateTimeConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }

    private sealed class DecimalStringConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetDecimal();

            if (reader.TokenType == JsonTokenType.String
                && decimal.TryParse(reader.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value))
                return value;

            throw new JsonException("Expected a decimal string such as \"12500.0000\".");
        }

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue(StorageScale.Round(value).ToString("0.0000", CultureInfo.InvariantCulture));
    }

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && DateTime.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
                return value;

            throw new JsonException("Expected an ISO-8601 timestamp.");
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new JsonException("Only UTC times may be serialised.");

            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}
