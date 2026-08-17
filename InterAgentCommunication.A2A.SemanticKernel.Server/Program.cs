using A2A.AspNetCore;
using InterAgentCommunication.A2A.SemanticKernel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(Settings.Kernel);
builder.Services.AddA2AAgent<WeatherAgentHandler>(WeatherAgentHandler.GetAgentCard());
var app = builder.Build();

// MapA2A maps both the JSON-RPC endpoint and the well-known agent card from DI.
app.MapA2A("/weather");
await app.RunAsync("http://localhost:5100");