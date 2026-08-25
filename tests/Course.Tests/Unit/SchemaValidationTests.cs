using System.Text.Json.Nodes;
using Course.Infrastructure.Validation;

namespace Course.Tests.Unit;

public sealed class SchemaValidationTests
{
    // Shape copied from contracts/course-1/operation-get.payload.schema.json's
    // operationId field. Deliberately has no $id: Json.Schema.Net registers any
    // schema that declares one into a process-wide registry and throws on a
    // second registration of the same $id, which every other JsonSchemaValidator
    // instance across the test run would collide with.
    private static readonly JsonNode UuidFieldSchema = JsonNode.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["operationId"],
          "properties": { "operationId": { "type": "string", "format": "uuid" } }
        }
        """)!;

    // Shape copied from contracts/course-1/payment-request.payload.schema.json's
    // amount pattern.
    private static readonly JsonNode AmountFieldSchema = JsonNode.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "additionalProperties": false,
          "required": ["amount"],
          "properties": {
            "amount": {
              "type": "string",
              "pattern": "^(?:0\\.0[1-9]|0\\.[1-9][0-9]?|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"
            }
          }
        }
        """)!;

    // In draft 2020-12, "format" is an annotation-only keyword unless the
    // evaluator turns on format assertion. Without JsonSchemaValidator's
    // RequireFormatValidation=true, this instance would pass validation here
    // and only fail later, as a 500, once the database rejects the string cast.
    [Fact]
    public void NonUuidOperationId_FailsValidation()
    {
        var validator = new JsonSchemaValidator();
        var instance = JsonNode.Parse("""{"operationId": "not-a-uuid"}""")!;

        var result = validator.Validate(UuidFieldSchema, instance);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("0.00", false)] // excluded by the amount >= 0.01 floor
    [InlineData("0.001", false)] // three decimal digits, more precision than allowed
    [InlineData("0.01", true)] // the lowest accepted amount
    [InlineData("12345678901234567", false)] // 17 digits before the point, over the 16-digit ceiling
    public void AmountBoundaries_MatchPaymentRequestPattern(string amount, bool expectedValid)
    {
        var validator = new JsonSchemaValidator();
        var instance = new JsonObject { ["amount"] = amount };

        var result = validator.Validate(AmountFieldSchema, instance);

        Assert.Equal(expectedValid, result.IsValid);
    }
}
