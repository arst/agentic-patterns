using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace Planning.SemanticKernel;

public sealed class TravelTools
{
    [KernelFunction]
    [Description("Search available flights")]
    public string GetFlights(
        [Description("Origin airport code")] string from,
        [Description("Destination airport code")]
        string to,
        [Description("Departure date ISO yyyy-MM-dd")]
        string date)
    {
        return $"[Flights] {from}->{to} on {date}: F100 09:00, F200 13:30";
    }

    [KernelFunction]
    [Description("Book a flight by flight id")]
    public string BookFlight([Description("Flight id")] string flightId)
    {
        return $"[Booked] {flightId} (confirmation: ABC123)";
    }

    [KernelFunction]
    [Description("Draft an email confirmation")]
    public string DraftEmail([Description("Confirmation text")] string confirmation)
    {
        return $"Subject: Flight booked\n\n{confirmation}\n\nThanks!";
    }
}