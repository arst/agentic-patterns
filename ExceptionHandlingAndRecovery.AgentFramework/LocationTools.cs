using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace ExceptionHandlingAndRecovery.AgentFramework;

internal static class LocationTools
{
    /// The geocoder's own deadline. It is OURS, not the caller's — blowing it is a dependency
    /// failure, so it must not reach the retry policy dressed as an OperationCanceledException.
    private static readonly TimeSpan LookupDeadline = TimeSpan.FromMilliseconds(200);

    public static AIFunction PreciseLookup(DependencyCircuitBreaker circuitBreaker) =>
        AIFunctionFactory.Create((string address, CancellationToken cancellationToken) =>
            circuitBreaker.ExecuteAsync(ct => GetPreciseLocation(address, ct), cancellationToken),
            "GetPreciseLocation", "Get precise lat/lng and metadata for an address.");
    public static AIFunction GeneralLookup => AIFunctionFactory.Create(GetGeneralAreaInfo);

    private static async Task<string> GetPreciseLocation(string address, CancellationToken cancellationToken)
    {
        // Simulate a flaky external geocoding API
        if (Random.Shared.NextDouble() < 0.5)
            throw new HttpRequestException("503 — Geocoding service temporarily unavailable");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LookupDeadline);
        try
        {
            // Simulate network latency — sometimes slower than the deadline above.
            await Task.Delay(Random.Shared.Next(50, 400), deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The DEPENDENCY blew OUR deadline; the caller never asked for anything to stop.
            // Translating it here is what lets the retry policy and the circuit breaker treat
            // it as the transient failure it is instead of as caller intent.
            throw new TimeoutException(
                $"Geocoding lookup exceeded {LookupDeadline.TotalMilliseconds:N0}ms.");
        }

        return $$"""{ "address": "{{address}}", "lat": 48.8566, "lng": 2.3522, "confidence": "high" }""";
    }

    [Description("Get general area info for a city. More reliable, less precise.")]
    private static Task<string> GetGeneralAreaInfo(string city)
    {
        return Task.FromResult(
            $$"""{ "city": "{{city}}", "region": "Île-de-France", "country": "France", "confidence": "low" }""");
    }
}
