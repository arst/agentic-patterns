using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Shared;

var loggerBuilder = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var client = new A2AClient(new Uri("http://localhost:5200/a2a/weather/"));
var a2AAgent = new A2AAgent(client, loggerFactory: loggerBuilder);

var agentAsFunction = async (string question) =>
{
    Console.WriteLine($"[A2A] Calling WeatherExpert: \"{question}\"");
    var result = await a2AAgent.RunAsync(
        question);
    return result.Text;
};

var weatherTool = AIFunctionFactory.Create(agentAsFunction, "WeatherExpert",
    "Ask the WeatherExpert agent a question about the weather at a specific location and time.");

var chatClient = Settings.ChatClient;
var agent = new ChatClientAgent(chatClient,
    """
    You are a travel planning assistant. Help users plan trips by:
    1. Using the WeatherExpert tool to check weather at the destination
    2. Providing packing recommendations based on the forecast
    3. Suggesting the best time to visit
    Always check the weather before giving travel advice.
    """,
    "TravelPlanner",
    tools: [weatherTool]);

Console.WriteLine("\nUser: I'm planning a weekend trip to Amsterdam. What should I pack?\n");

var result = await agent.RunAsync(
    "I'm planning a weekend trip to Amsterdam. What should I pack?");

Console.WriteLine($"TravelPlanner:\n{result}");