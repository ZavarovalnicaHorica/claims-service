using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Horica.ClaimsService.Common;
using Horica.ClaimsService.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Horica.ClaimsService.Infrastructure.Policies;

public sealed class PolicyOptions
{
    public const string SectionName = "Policies";

    /// <summary>
    /// Base URL of PolicyService, e.g. http://policy-service:8080. Empty = do not look policies up
    /// (the claim is accepted as submitted; the agents still check the policy).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Path of one policy; {number} is replaced by the policy number.</summary>
    public string PolicyPath { get; set; } = "/policies/{number}";

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>When PolicyService returns a holderId, it must be the customer submitting the claim.</summary>
    public bool RequireHolderMatch { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>What ClaimsService needs to know about a policy.</summary>
public sealed record PolicyInfo(string Number, string? HolderId, string? Type);

public enum PolicyLookupStatus
{
    NotConfigured = 0,
    Found = 1,
    NotFound = 2
}

public sealed record PolicyLookup(PolicyLookupStatus Status, PolicyInfo? Policy);

/// <summary>
/// REST call to PolicyService (GET /policies/{number}) with the caller's token, so PolicyService applies its own
/// access rules. Reads the fields leniently: number | policyNumber, holderId | customerId, type.
/// </summary>
public sealed class PolicyClient
{
    private readonly HttpClient _http;
    private readonly PolicyOptions _options;
    private readonly ILogger<PolicyClient> _logger;

    public PolicyClient(HttpClient http, IOptions<PolicyOptions> options, ILogger<PolicyClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PolicyLookup> LookupAsync(string policyNumber, string? bearerToken, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            return new PolicyLookup(PolicyLookupStatus.NotConfigured, null);
        }

        var url = _options.BaseUrl!.TrimEnd('/') + "/" +
                  _options.PolicyPath.TrimStart('/').Replace("{number}", Uri.EscapeDataString(policyNumber));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("PolicyService is not reachable at {Url}: {Reason}", url, ex.Message);
            throw new DependencyUnavailableException("PolicyService is not reachable. Try again later.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new PolicyLookup(PolicyLookupStatus.NotFound, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("PolicyService answered {Status} for {Url}.", (int)response.StatusCode, url);
                throw new DependencyUnavailableException(
                    "PolicyService could not check the policy (HTTP " + (int)response.StatusCode + ").");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var policy = Parse(body, policyNumber);
            return policy is null
                ? new PolicyLookup(PolicyLookupStatus.NotFound, null)
                : new PolicyLookup(PolicyLookupStatus.Found, policy);
        }
    }

    /// <summary>Accepts one policy object, or a list of policies (the one with the number is used).</summary>
    public static PolicyInfo? Parse(string? json, string policyNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var candidate = ReadPolicy(item);
                    if (candidate is not null &&
                        string.Equals(candidate.Number, policyNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }

                return null;
            }

            var policy = ReadPolicy(root);
            if (policy is null)
            {
                return null;
            }

            // A single object without a number is taken as the requested policy.
            return policy.Number.Length == 0 ? policy with { Number = policyNumber } : policy;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PolicyInfo? ReadPolicy(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var number = JsonRead.ReadString(item, "number", "policyNumber", "policyId") ?? string.Empty;
        var holderId = JsonRead.ReadString(item, "holderId", "customerId", "ownerId", "holder_id");
        var type = JsonRead.ReadString(item, "type", "claimType", "coverageType");

        return new PolicyInfo(number, holderId, type?.ToUpperInvariant());
    }
}
