using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace ExceptionHandlingAndRecovery.AgentFramework;

internal static class LocationTools
{
    public static AIFunction PreciseLookup(DependencyCircuitBreaker circuitBreaker) =>
        AIFunctionFactory.Create((string address, CancellationToken cancellationToken) =>
            circuitBreaker.ExecuteAsync(ct => GetPreciseLocation(address, ct), cancellationToken),
            "GetPreciseLocation", "Get precise lat/lng and metadata for an address.");
    public static AIFunction GeneralLookup => AIFunctionFactory.Create(GetGeneralAreaInfo);

    private static async Task<string> GetPreciseLocation(string address, CancellationToken cancellationToken)
    {
        // Simulate a flaky external geocoding API
        if (Random.Shared.NextDouble() < 0.6)
            throw new HttpRequestException("503 — Geocoding service temporarily unavailable");

        await Task.Delay(50, cancellationToken); // simulate network latency
        return $$"""{ "address": "{{address}}", "lat": 48.8566, "lng": 2.3522, "confidence": "high" }""";
    }

    [Description("Get general area info for a city. More reliable, less precise.")]
    private static Task<string> GetGeneralAreaInfo(string city)
    {
        return Task.FromResult(
            $$"""{ "city": "{{city}}", "region": "Île-de-France", "country": "France", "confidence": "low" }""");
    }
}
