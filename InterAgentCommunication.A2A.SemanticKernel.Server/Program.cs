using A2A;
using A2A.AspNetCore;
using InterAgentCommunication.A2A.SemanticKernel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddA2AAgent<WeatherAgentHandler>(WeatherAgentHandler.GetAgentCard());
builder.Services.AddSingleton<IAgentHandler>(_ => new WeatherAgentHandler(Settings.Kernel));
builder.Services.AddSingleton(WeatherAgentHandler.GetAgentCard());
builder.Services.AddSingleton(new A2AServerOptions());
builder.Services.TryAddSingleton<ChannelEventNotifier>();
builder.Services.TryAddSingleton<ITaskStore, InMemoryTaskStore>();
builder.Services.TryAddSingleton<IA2ARequestHandler>(sp =>
    new A2AServer(
        sp.GetRequiredService<IAgentHandler>(),
        sp.GetRequiredService<ITaskStore>(),
        sp.GetRequiredService<ChannelEventNotifier>(),
        sp.GetRequiredService<ILogger<A2AServer>>(),
        sp.GetRequiredService<A2AServerOptions>()));
var app = builder.Build();

app.MapA2A("/weather");
app.MapWellKnownAgentCard(WeatherAgentHandler.GetAgentCard());
await app.RunAsync("http://localhost:5100");