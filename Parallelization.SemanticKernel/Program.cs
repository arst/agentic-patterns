using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var kernel = Settings.Kernel;

const string prompt = "Assess launching a new B2B analytics product in the EU. Provide recommendations.";

ChatCompletionAgent researcher = new()
{
    Name = "Researcher",
    Instructions = "You are a factual researcher. Provide concise findings, risks, and unknowns.",
    Kernel = kernel
};

ChatCompletionAgent marketer = new()
{
    Name = "Marketer",
    Instructions = "You are a marketing strategist. Propose positioning, messaging, and target personas.",
    Kernel = kernel
};

ChatCompletionAgent legal = new()
{
    Name = "Legal",
    Instructions = "You are a cautious compliance reviewer. Flag legal/policy concerns and needed disclaimers.",
    Kernel = kernel
};

async Task<string> RunAgentAsync(ChatCompletionAgent agent, string input)
{
    var responses = new List<string>();
    await foreach (ChatMessageContent response in agent.InvokeAsync(new ChatMessageContent(AuthorRole.User, input)))
        responses.Add(response.ToChatMessage().Text.Trim());
    return $"## {agent.Name}{Environment.NewLine}{string.Join(Environment.NewLine, responses)}";
}

var tasks = new[]
{
    RunAgentAsync(researcher, prompt),
    RunAgentAsync(marketer, prompt),
    RunAgentAsync(legal, prompt)
};

var outputs = await Task.WhenAll(tasks);

Console.WriteLine(string.Join(
    $"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}",
    outputs));