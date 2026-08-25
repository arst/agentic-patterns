using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;

namespace Planning.SemanticKernel;

public sealed class TravelTools
{
    // Minted once per instance (i.e. once per plan run) so a retried BookFlight call replays
    // instead of booking twice.
    private readonly string _bookingKey = Guid.NewGuid().ToString("N");

    // ponytail: in-memory dictionary standing in for a real idempotent booking service; swap for
    // durable keyed storage (see IdempotentToolCalls) if this needs to survive a process restart.
    private readonly Dictionary<string, string> _bookings = new(StringComparer.Ordinal);

    [KernelFunction]
    [Description("Search available flights, priced")]
    public string GetFlights(
        [Description("Origin airport code")] string from,
        [Description("Destination airport code")]
        string to,
        [Description("Departure date ISO yyyy-MM-dd")]
        string date)
    {
        // Priced options - "cheapest" is now answerable from evidence.
        return JsonSerializer.Serialize(new[]
        {
            new FlightOption("F100", "09:00", 189.00m),
            new FlightOption("F200", "13:30", 142.50m),
            new FlightOption("F300", "19:45", 167.25m)
        });
    }

    [KernelFunction]
    [Description("Deterministically select the cheapest flight from a priced FlightOption[] JSON array. " +
                  "The only way to choose a flight - never pick one from free text.")]
    public string SelectCheapest([Description("JSON array of FlightOption returned by GetFlights")] string flights) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<FlightOption[]>(flights)!
            .MinBy(f => f.PriceEur) ?? throw new InvalidOperationException("No flights to choose from."));

    [KernelFunction]
    [Description("Request human approval to book the exact flight and price")]
    public string RequestBookingApproval([Description("JSON FlightOption to approve")] string flight)
    {
        var option = JsonSerializer.Deserialize<FlightOption>(flight)!;
        Console.Write($"Approve booking {option.FlightId} at EUR {option.PriceEur:F2}? (y/n): ");
        var answer = Console.ReadLine(); // EOF -> null -> denied, never auto-approved
        var approved = answer?.Trim().ToLowerInvariant() is "y" or "yes";
        if (!approved)
            throw new InvalidOperationException("Booking was not approved; the plan stops here.");
        return flight;
    }

    [KernelFunction]
    [Description("Book an approved flight. Idempotent: replays the first result for a retried call.")]
    public string BookFlight([Description("JSON of the approved FlightOption")] string approvedFlight)
    {
        var option = JsonSerializer.Deserialize<FlightOption>(approvedFlight)!;
        if (_bookings.TryGetValue(_bookingKey, out var existing)) return existing; // replay, no second booking
        return _bookings[_bookingKey] =
            $"[Booked] {option.FlightId} at EUR {option.PriceEur:F2} (confirmation: ABC123)";
    }

    [KernelFunction]
    [Description("Draft an email confirmation")]
    public string DraftEmail([Description("Confirmation text")] string confirmation)
    {
        return $"Subject: Flight booked\n\n{confirmation}\n\nThanks!";
    }
}
