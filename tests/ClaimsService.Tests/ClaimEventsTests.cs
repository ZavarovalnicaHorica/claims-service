using System.Text.Json;
using Horica.ClaimsService.Application;
using Horica.ClaimsService.Domain;
using Horica.ClaimsService.Infrastructure.Messaging;
using Xunit;

namespace Horica.ClaimsService.Tests;

public class ClaimEventsTests
{
    private static readonly KafkaOptions Kafka = new KafkaOptions();
    private const string Source = "/horica/claims-service";

    [Fact]
    public void Submit_writes_claim_submitted_v1()
    {
        var claim = TestData.NewClaim(1200m);

        var message = Assert.Single(ClaimEvents.ToOutbox(claim, Kafka, Source, TestData.Now));

        Assert.Equal("claim.submitted.v1", message.Topic);
        Assert.Equal("claim.submitted.v1", message.EventType);
        Assert.Equal("CLM-1001", message.MessageKey);

        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("specversion").GetString());
        Assert.Equal(message.Id.ToString(), root.GetProperty("id").GetString());
        Assert.Equal(Source, root.GetProperty("source").GetString());
        Assert.Equal("claims/CLM-1001", root.GetProperty("subject").GetString());

        var data = root.GetProperty("data");
        Assert.Equal("CLM-1001", data.GetProperty("claimId").GetString());
        Assert.Equal("POL-1001", data.GetProperty("policyNumber").GetString());
        Assert.Equal("cust-1", data.GetProperty("customerId").GetString());
        Assert.Equal(1200m, data.GetProperty("claimedAmount").GetDecimal());
        Assert.Equal("2026-11-01", data.GetProperty("incidentDate").GetString());
        Assert.Equal(2, data.GetProperty("documents").GetArrayLength());
        Assert.Equal(1, data.GetProperty("analysisRound").GetInt32());
        Assert.False(data.GetProperty("reanalysis").GetBoolean());
    }

    [Fact]
    public void Manual_review_writes_review_requested_v1_in_the_shape_review_service_reads()
    {
        var claim = TestData.NewClaim(1200m);
        TestData.Clear(claim);
        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.45), TestData.Rules, TestData.Now);

        var messages = ClaimEvents.ToOutbox(claim, Kafka, Source, TestData.Now);

        Assert.Equal(new[] { "claim.status.changed.v1", "review.requested.v1" }, messages.Select(m => m.EventType));
        Assert.Equal(new[] { 0, 1 }, messages.Select(m => m.Sequence));

        var review = messages.Single(m => m.EventType == "review.requested.v1");
        Assert.Equal("review.requested.v1", review.Topic);
        Assert.Equal("CLM-1001", review.MessageKey);

        using var document = JsonDocument.Parse(review.Payload);
        var data = document.RootElement.GetProperty("data");

        // Fields read by review-service (ReviewRequestParser).
        Assert.Equal("CLM-1001", data.GetProperty("claimId").GetString());
        Assert.Equal("POL-1001", data.GetProperty("policyId").GetString());
        Assert.Equal("cust-1", data.GetProperty("customerId").GetString());
        Assert.Equal(1200m, data.GetProperty("claimedAmount").GetDecimal());
        Assert.Equal("EUR", data.GetProperty("currency").GetString());
        Assert.Equal(0.45, data.GetProperty("riskScore").GetDouble());
        Assert.Equal("APPROVE", data.GetProperty("recommendation").GetString());
        Assert.Equal("Policy valid, documents match.", data.GetProperty("summary").GetString());

        var riskReasons = data.GetProperty("riskReasons").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Amount is 40 % of the policy limit", riskReasons);
        Assert.Contains("Amount 1.200 € is above the auto-approval limit of 300 €.", riskReasons);
    }

    [Fact]
    public void Status_changes_use_the_same_status_names_as_the_api()
    {
        var claim = TestData.ClaimInReview();
        claim.ApplyReview(TestData.Review(ReviewDecision.NeedsInfo, "Add a photo."), TestData.Now);

        var message = Assert.Single(ClaimEvents.ToOutbox(claim, Kafka, Source, TestData.Now));

        using var document = JsonDocument.Parse(message.Payload);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("NEEDS_INFO", data.GetProperty("status").GetString());
        Assert.Equal("NEEDS_REVIEW", data.GetProperty("previousStatus").GetString());
        Assert.Equal("Add a photo.", data.GetProperty("reason").GetString());
    }

    [Fact]
    public void The_claim_json_matches_the_web_ui_model()
    {
        var claim = TestData.NewClaim(1200m);
        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.45), TestData.Rules, TestData.Now);

        var json = JsonSerializer.Serialize(claim.ToDto(staffView: true), JsonDefaults.Options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("CLM-1001", root.GetProperty("id").GetString());
        Assert.Equal("POL-1001", root.GetProperty("policyNumber").GetString());
        Assert.Equal("CAR", root.GetProperty("type").GetString());
        Assert.Equal("2026-11-01", root.GetProperty("incidentDate").GetString());
        Assert.Equal(1200m, root.GetProperty("amount").GetDecimal());
        Assert.Equal("NEEDS_REVIEW", root.GetProperty("status").GetString());
        Assert.Equal(0.45, root.GetProperty("risk").GetProperty("score").GetDouble());
        Assert.Equal("APPROVE", root.GetProperty("recommendation").GetProperty("action").GetString());
        Assert.False(root.GetProperty("recommendation").GetProperty("automatic").GetBoolean());
        Assert.Equal("PASS", root.GetProperty("agents")[0].GetProperty("outcome").GetString());
        Assert.Equal("Data agent", root.GetProperty("agents")[0].GetProperty("agent").GetString());
        Assert.Equal("damage-photo.jpg", root.GetProperty("documents")[0].GetProperty("name").GetString());
        Assert.Equal("cust-1", root.GetProperty("documents")[0].GetProperty("uploadedById").GetString());
        Assert.False(root.TryGetProperty("review", out _));
    }

    [Fact]
    public void Customers_do_not_see_internal_history()
    {
        var claim = TestData.NewClaim(1200m);
        claim.ApplyRecommendation(TestData.Recommendation(riskScore: 0.45), TestData.Rules, TestData.Now);
        // ToDto reads the stored history; here it is still in NewHistory.
        claim.History.AddRange(claim.NewHistory);

        var staff = claim.ToDto(staffView: true);
        var customer = claim.ToDto(staffView: false);

        Assert.Contains(staff.History, h => h.Event == HistoryEvents.RiskAssessed);
        Assert.DoesNotContain(customer.History, h => h.Event == HistoryEvents.RiskAssessed);
        Assert.DoesNotContain(customer.History, h => h.Event == HistoryEvents.ReviewReasons);
        Assert.Contains(customer.History, h => h.Event == HistoryEvents.ReviewRequested);
        Assert.True(staff.History.Count > customer.History.Count);
    }
}
