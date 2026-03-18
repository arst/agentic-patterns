using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace ExceptionHandlingAndRecovery.SemanticKernel;

public class LocationPlugin
{
    [KernelFunction]
    [Description("Get precise lat/lng and metadata for an address.")]
    public async Task<string> GetPreciseLocation(string address)
    {
        // Simulate a flaky external API
        if (Random.Shared.NextDouble() < 0.6)
            throw new HttpRequestException("503 — Geocoding service temporarily unavailable");

        return $"{{ \"address\": \"{address}\", \"lat\": 48.8566, \"lng\": 2.3522, \"confidence\": \"high\" }}";
    }

    [KernelFunction]
    [Description("Get general area information for a city name.")]
    public Task<string> GetGeneralAreaInfo(string city)
    {
        return Task.FromResult(
            $"{{ \"city\": \"{city}\", \"region\": \"Île-de-France\", \"country\": \"France\", \"confidence\": \"low\" }}");
    }
}