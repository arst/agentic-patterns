using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

var agent = new ChatClientAgent(Settings.ChatClient,
    "You are a helpful assistant. Use tools when needed.",
    tools: new List<AITool>
    {
        AIFunctionFactory.Create(GetWeather)
    });

var answer = await agent.RunAsync("What is the weather like in Amsterdam?");
Console.WriteLine(answer);
return;

static string GetWeather(string location)
{
    return $"Weather in {location}: cloudy, 15°C";
}