using System.ComponentModel;
using Microsoft.SemanticKernel;

public sealed class WeatherPlugin
{
    [KernelFunction]
    [Description("Get the current weather for a city.")]
    public string GetWeather(
        [Description("City name, e.g. 'Amsterdam'")]
        string city)
    {
        // Replace with real API call in production
        return $"Weather in {city}: cloudy, 15°C";
    }
}