using System.Diagnostics;
using A2A;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning) // keep noise down globally
    .AddFilter("Microsoft.Agents", LogLevel.Trace)
    .AddFilter("Microsoft.SemanticKernel", LogLevel.Trace)
    .AddFilter("A2A", LogLevel.Trace)
    .AddTraceSource("Microsoft.Agents", new ConsoleTraceListener());
var loggerFactory = LoggerFactory.Create(loggingBuilder =>
{
    loggingBuilder
        .AddConsole()
        .SetMinimumLevel(LogLevel.Trace)
        .AddFilter("Microsoft.Agents", LogLevel.Trace)
        .AddFilter("Microsoft.SemanticKernel", LogLevel.Trace)
        .AddFilter("A2A", LogLevel.Trace);
});
var chatClient = new Settings().ChatClient.AsBuilder().UseLogging(loggerFactory).Build();
builder.Services.AddSingleton(chatClient);

var weatherAgent = builder.AddAIAgent("weather",
    "You are a weather specialist. Provide realistic forecasts with temperature, precipitation, and clothing recommendations.");

var app = builder.Build();

app.MapA2A(weatherAgent, "/a2a/weather", new AgentCard
{
    Name = "WeatherExpert",
    Description = "Provides weather forecasts for any location worldwide.",
    Version = "1.0.0",
    PreferredTransport = AgentTransport.JsonRpc,
    Url = "http://localhost:5100/a2a/weather"
});

app.Run("http://localhost:5200");