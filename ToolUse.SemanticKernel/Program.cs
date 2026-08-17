using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shared;

// Local kernel so the shared Settings.Kernel singleton isn't mutated by the plugin import
var kernel = Settings.CreateKernelBuilder().Build();

kernel.ImportPluginFromType<WeatherPlugin>();

var settings = new OpenAIPromptExecutionSettings
{
    FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
};

var agent = new ChatCompletionAgent
{
    Name = "WeatherAssistant",
    Instructions = "You are helpful. Use tools when needed. If you call a tool, use its result in the final answer.",
    Kernel = kernel,
    Arguments = new KernelArguments(settings)
};

// 4) Run
var response = agent.InvokeAsync("What's the weather like in Amsterdam?");
await foreach (var r in response) Console.WriteLine(r.Message.Content);