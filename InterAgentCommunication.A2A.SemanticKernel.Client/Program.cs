using A2A;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var resolver = new A2ACardResolver(new Uri("http://localhost:5100"));
var weatherCard = await resolver.GetAgentCardAsync();
Console.WriteLine($"Discovered remote agent: {weatherCard.Name}");
var a2AClient = new A2AClient(new Uri(weatherCard.SupportedInterfaces[0].Url));

var kernel = Settings.Kernel;

kernel.Plugins.AddFromFunctions("RemoteAgents", [
    KernelFunctionFactory.CreateFromMethod(
        async (string query) =>
        {
            Console.WriteLine($"  [A2A] Calling WeatherExpert: \"{query}\"");
            var response = await a2AClient.SendMessageAsync(new SendMessageRequest
            {
                Message = new Message
                {
                    Role = Role.User,
                    MessageId = Guid.NewGuid().ToString(),
                    Parts = [new Part { Text = query }]
                }
            });
            // ponytail: Microsoft.SemanticKernel.Agents.A2A 1.79.0-alpha targets A2A 0.3.1-preview and
            // breaks at runtime against the A2A 1.0.0-preview2 pinned here, so we keep the manual protocol
            // code; swap to its A2AAgent once the wrapper catches up with the 1.0 protocol surface.
            return response.Task?.Artifacts is [{ Parts: [{ Text: { Length: > 0 } text }, ..] }, ..]
                ? text
                : "The WeatherExpert agent returned no answer.";
        },
        "GetWeatherForecast",
        "Get a weather forecast from the remote WeatherExpert agent. Use this for any weather-related questions.")
]);

var chat = kernel.GetRequiredService<IChatCompletionService>();
var settings = new PromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var history = new ChatHistory("""
                              You are a travel planning assistant. Help users plan trips by:
                              1. Using the GetWeatherForecast tool to check weather at the destination
                              2. Providing packing recommendations based on the forecast
                              3. Suggesting the best time to visit
                              Always check the weather before giving travel advice.
                              """);

Console.WriteLine("\n?? User: I'm planning a weekend trip to Amsterdam. What should I pack?\n");
history.AddUserMessage("I'm planning a weekend trip to Amsterdam. What should I pack?");

var response = await chat.GetChatMessageContentAsync(history, settings, kernel);
Console.WriteLine($"TravelPlanner:\n{response.Content}");