#pragma warning disable MEAI001
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Logging
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(Settings.ChatClient);

var weatherAgent = builder.AddAIAgent("WeatherExpert",
    "You are a weather specialist. Provide realistic forecasts with temperature, precipitation, and clothing recommendations.");

// The hosting layer generates the agent card (name, URL, capabilities) from the
// registered agent and the request address - no hand-built AgentCard needed.
weatherAgent.AddA2AServer(_ => { });

var app = builder.Build();

app.MapA2AJsonRpc(weatherAgent, "/a2a/weather");

app.Run("http://localhost:5200");
