using A2A;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InterAgentCommunication.A2A.SemanticKernel.Server;

public class WeatherAgentHandler : IAgentHandler
{
    private readonly ChatCompletionAgent _agent;

    public WeatherAgentHandler(Kernel kernel)
    {
        _agent = new ChatCompletionAgent
        {
            Name = "WeatherExpert",
            Instructions = """
                           You are a weather specialist. Provide detailed forecasts
                           including temperature, precipitation, and recommendations.
                           If you don't have real data, simulate a realistic forecast.
                           """,
            Kernel = kernel
        };
    }

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(cancellationToken);
        await updater.StartWorkAsync(cancellationToken: cancellationToken);

        var artifactParts = new List<Part>();
        await foreach (var response in _agent.InvokeAsync(context.UserText ?? string.Empty,
                           cancellationToken: cancellationToken))
        {
            var content = response.Message.Content;
            if (!string.IsNullOrEmpty(content))
                artifactParts.Add(Part.FromText(content));
        }

        await updater.AddArtifactAsync(artifactParts, cancellationToken: cancellationToken);
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }

    public static AgentCard GetAgentCard()
    {
        return new AgentCard
        {
            Name = "WeatherExpert",
            Description = "Provides detailed weather forecasts for any location worldwide.",
            Version = "1.0.0",
            SupportedInterfaces =
            [
                new AgentInterface
                {
                    Url = "http://localhost:5100/weather",
                    ProtocolBinding = "JSONRPC",
                    ProtocolVersion = "1.0"
                }
            ],
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Capabilities = new AgentCapabilities { Streaming = false },
            Skills =
            [
                new AgentSkill
                {
                    Id = "weather_forecast",
                    Name = "Weather Forecast",
                    Description = "Get current weather and 5-day forecasts for any location.",
                    Tags = ["weather", "forecast", "temperature"]
                }
            ]
        };
    }
}