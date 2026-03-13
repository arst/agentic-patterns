using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

namespace Shared;

public class Settings
{
    private readonly IConfigurationRoot _root = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", false)
        .AddUserSecrets<Settings>(true)
        .AddEnvironmentVariables()
        .Build();

    private AzureOpenAiSettiings? _azureOpenAi;

    public AzureOpenAiSettiings AzureOpenAi => _azureOpenAi ??=
        _root.GetSection("AzureOpenAI").Get<AzureOpenAiSettiings>()
        ?? new AzureOpenAiSettiings();

    public Kernel Kernel => Kernel.CreateBuilder()
        .AddAzureOpenAIChatCompletion(
            AzureOpenAi.ChatModelDeployment,
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey)
        .Build();

    public IChatClient ChatClient => new AzureOpenAIClient(
            new Uri(AzureOpenAi.Endpoint),
            new ApiKeyCredential(AzureOpenAi.ApiKey))
        .GetChatClient(AzureOpenAi.ChatModelDeployment)
        .AsIChatClient();
}