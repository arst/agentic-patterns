using System.ComponentModel;
using System.Data;
using Microsoft.SemanticKernel;

namespace ReasoningAndActing;

public class ResearchTools
{
    [KernelFunction]
    [Description("Get the approximate population of a country.")]
    public static string GetPopulation(
        [Description("Country name")] string country)
    {
        // Simulated lookup — in production, call a real API
        return country.ToLower() switch
        {
            "canada" => "Approximately 40.1 million (2024 estimate)",
            "australia" => "Approximately 26.5 million (2024 estimate)",
            "united states" or "usa" => "Approximately 334 million (2024 estimate)",
            _ => $"Population data not available for {country}"
        };
    }

    [KernelFunction]
    [Description("Perform a mathematical calculation. Input: a math expression like '40.1 / 26.5'.")]
    public static string Calculate(
        [Description("Math expression to evaluate")]
        string expression)
    {
        try
        {
            // Simple evaluation — in production, use a proper expression parser
            var result = new DataTable().Compute(expression, null);
            return $"{expression} = {result}";
        }
        catch
        {
            return $"Could not evaluate: {expression}";
        }
    }
}