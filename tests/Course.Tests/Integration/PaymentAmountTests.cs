using System.Text.Json.Nodes;

namespace Course.Tests.Integration;

[Collection("integration")]
public sealed class PaymentAmountTests(ApiFactory factory)
{
    // core.operations stores the amount as numeric(18,2) and core.operation_to_result
    // renders it back with to_char(..., 'FM99999999999999990.00'), so the response
    // carries the canonical two-decimal form of the submitted value rather than the
    // submitted string itself: "1" comes back as "1.00". Both forms satisfy
    // operation.result.schema.json's amount pattern and the value is unchanged.
    [Theory]
    [InlineData("1", "1.00")]
    [InlineData("1.2", "1.20")]
    [InlineData("0.01", "0.01")]
    [InlineData("10.00", "10.00")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]
    public async Task SubmittedAmount_IsEchoedInCanonicalTwoDecimalForm(string amount, string expected)
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var response = await client.PostActionAsync(
            "/api/payment/request",
            token,
            new JsonObject { ["operationKind"] = "PAYMENT_APPROVAL", ["amount"] = amount, ["currency"] = "RUB" },
            $"amount-{Guid.NewGuid():N}");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expected, response.Body["result"]!["amount"]!.GetValue<string>());
    }

    // operation.get renders through the same core.operation_to_result, so a later
    // read shows the same canonical form the write returned.
    [Fact]
    public async Task StoredAmount_ReadsBackInTheSameForm()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var created = await client.PostActionAsync(
            "/api/payment/request",
            token,
            new JsonObject { ["operationKind"] = "PAYMENT_APPROVAL", ["amount"] = "1.2", ["currency"] = "RUB" },
            $"amount-get-{Guid.NewGuid():N}");

        Assert.Equal(200, created.StatusCode);
        var operationId = created.Body["result"]!["operationId"]!.GetValue<string>();

        var fetched = await client.PostActionAsync(
            "/api/operation/get", token, new JsonObject { ["operationId"] = operationId });

        Assert.Equal(200, fetched.StatusCode);
        Assert.Equal("1.20", created.Body["result"]!["amount"]!.GetValue<string>());
        Assert.Equal("1.20", fetched.Body["result"]!["amount"]!.GetValue<string>());
    }

    // autocheck.operations projects the numeric column the checker reads
    // (contract-reference: "operations | ... amount numeric ...").
    [Fact]
    public async Task NumericColumn_StillHoldsTheValue()
    {
        using var client = factory.CreateClient();
        var token = ApiFactory.CreateToken("candidate-client", "web", "payment:write payment:read");

        var created = await client.PostActionAsync(
            "/api/payment/request",
            token,
            new JsonObject { ["operationKind"] = "PAYMENT_APPROVAL", ["amount"] = "1.2", ["currency"] = "RUB" },
            $"amount-numeric-{Guid.NewGuid():N}");

        Assert.Equal(200, created.StatusCode);
        var operationId = Guid.Parse(created.Body["result"]!["operationId"]!.GetValue<string>());

        var stored = await factory.Postgres.ScalarAsOwnerAsync<decimal>(
            "select amount from autocheck.operations where operation_id = @operationId",
            ("operationId", operationId));

        Assert.Equal(1.20m, stored);
    }
}
