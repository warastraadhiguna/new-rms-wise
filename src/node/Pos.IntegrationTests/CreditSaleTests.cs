using System.Net;
using System.Text.Json;
using Dapper;
using Pos.IntegrationTests.Infrastructure;

namespace Pos.IntegrationTests;

/// <summary>
/// KREDIT as the desktop sends it (Form Bayar Nota): DP as a CASH payment (only when above 0) plus the rest as
/// CREDIT, so the receivable is exactly that rest. Cash rounding applies only when a DP is paid.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CreditSaleTests(DatabaseFixture db) : IAsyncLifetime
{
    private NodeServiceFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.IsConfigured)
        {
            _factory = new NodeServiceFactory(db);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private async Task<Guid> OpenShiftAsync()
    {
        var device = await db.CreateDeviceAsync();
        var (status, _) = await _client.PostAsync("/api/v1/shifts/open", new { OpeningCash = 100000m }, db.CashierToken(), device);
        Assert.Equal(HttpStatusCode.Created, status);
        return device;
    }

    /// <summary>One line of 13.680 (cash rounding step 100 → 13.700 when cash is paid).</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> SellAsync(Guid? customer, params object[] payments)
    {
        var product = await db.CreateSellableProductAsync(13680m, stock: [(null, 5m)]);
        var device = await OpenShiftAsync();
        return await _client.PostAsync("/api/v1/sales", new
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customer,
            Lines = new[] { new { product.ProductUnitId, Qty = 1m } },
            Payments = payments,
        }, db.CashierToken(), device);
    }

    private async Task<decimal> ReceivableAsync(Guid customer)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE(SUM(credit_amount), 0) FROM sales WHERE customer_id = @customer", new { customer });
    }

    [DatabaseFact]
    public async Task Dp_0_puts_the_whole_grand_total_on_credit_without_cash_rounding()
    {
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m);

        var (status, body) = await SellAsync(customer, new { Method = "CREDIT", Amount = 13680m });

        Assert.Equal(HttpStatusCode.Created, status);
        var sale = body.GetProperty("sale");
        Assert.Equal(13680m, sale.Dec("total"));
        Assert.Equal(0m, sale.Dec("rounding_amount"));
        Assert.Equal(13680m, sale.Dec("credit_amount"));
        Assert.Equal(JsonValueKind.String, sale.GetProperty("due_date").ValueKind);
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal(13680m, await ReceivableAsync(customer));

        var (_, detail) = await _client.GetAsync($"/api/v1/sales/{sale.Id()}", db.CashierToken());
        var payment = Assert.Single(detail.GetProperty("payments").EnumerateArray());
        Assert.Equal(("CREDIT", 13680m), (payment.Str("method"), payment.Dec("amount")));
    }

    [DatabaseFact]
    public async Task Dp_is_paid_in_cash_and_only_the_rest_becomes_receivable()
    {
        var customer = await db.CreateCustomerAsync(creditLimit: 1000000m);

        // DP 5.000 in cash → total rounded to 13.700 → credit 8.700
        var (status, body) = await SellAsync(customer,
            new { Method = "CASH", Amount = 5000m }, new { Method = "CREDIT", Amount = 8700m });

        Assert.Equal(HttpStatusCode.Created, status);
        var sale = body.GetProperty("sale");
        Assert.Equal(13700m, sale.Dec("total"));
        Assert.Equal(20m, sale.Dec("rounding_amount"));
        Assert.Equal(8700m, sale.Dec("credit_amount"));
        Assert.Equal(0m, sale.Dec("change"));
        Assert.Equal(8700m, await ReceivableAsync(customer));

        var (_, detail) = await _client.GetAsync($"/api/v1/sales/{sale.Id()}", db.CashierToken());
        var payments = detail.GetProperty("payments").EnumerateArray().ToList();
        var cash = Assert.Single(payments, p => p.Str("method") == "CASH");
        var credit = Assert.Single(payments, p => p.Str("method") == "CREDIT");
        Assert.Equal((5000m, 5000m, 0m), (cash.Dec("amount"), cash.Dec("tendered"), cash.Dec("change_given")));
        Assert.Equal(8700m, credit.Dec("amount"));
    }

    [DatabaseFact]
    public async Task Credit_without_a_member_is_refused()
    {
        var (status, body) = await SellAsync(null, new { Method = "CREDIT", Amount = 13680m });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("credit_requires_customer", body.Str("code"));
    }

    [DatabaseFact]
    public async Task Over_the_limit_is_saved_with_a_warning_that_carries_the_amounts()
    {
        var customer = await db.CreateCustomerAsync(creditLimit: 10000m);

        var (status, body) = await SellAsync(customer, new { Method = "CREDIT", Amount = 13680m });

        Assert.Equal(HttpStatusCode.Created, status);                    // never blocked
        var warning = Assert.Single(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal("credit_limit_exceeded", warning.Str("code"));
        Assert.Equal(10000m, warning.Dec("credit_limit"));
        Assert.Equal(13680m, warning.Dec("outstanding_after"));
        Assert.Equal(3680m, warning.Dec("exceeded_by"));
        Assert.Equal(13680m, await ReceivableAsync(customer));
    }
}
