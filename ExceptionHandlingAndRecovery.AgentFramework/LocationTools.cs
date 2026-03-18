using System.ComponentModel;
using Microsoft.Extensions.AI;

internal static class LocationTools
{
    // Expose tools as AIFunctions for the agent to use
    public static AIFunction PreciseLookup => AIFunctionFactory.Create(GetPreciseLocation);
    public static AIFunction GeneralLookup => AIFunctionFactory.Create(GetGeneralAreaInfo);

    [Description("Get precise lat/lng and metadata for an address.")]
    private static async Task<string> GetPreciseLocation(string address)
    {
        // Simulate a flaky external geocoding API
        if (Random.Shared.NextDouble() < 0.6)
            throw new HttpRequestException("503 — Geocoding service temporarily unavailable");

        await Task.Delay(50); // simulate network latency
        return $$"""{ "address": "{{address}}", "lat": 48.8566, "lng": 2.3522, "confidence": "high" }""";
    }

    [Description("Get general area info for a city. More reliable, less precise.")]
    private static Task<string> GetGeneralAreaInfo(string city)
    {
        return Task.FromResult(
            $$"""{ "city": "{{city}}", "region": "Île-de-France", "country": "France", "confidence": "low" }""");
    }
}