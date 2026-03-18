using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

#pragma warning disable SKEXP0010

namespace Shared;

public class Settings
{
    private readonly IConfigurationRoot _root = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", false)
        .AddUserSecrets<Settings>(true)
        .AddEnvironmentVariables()
        .Build();

    public Mem0ApiSettings Mem0ApiSettings => _root.GetSection("Mem0").Get<Mem0ApiSettings>() ?? new Mem0ApiSettings();

    public AzureOpenAiSettiings AzureOpenAi => field ??=
        _root.GetSection("AzureOpenAI").Get<AzureOpenAiSettiings>()
        ?? new AzureOpenAiSettiings();

    public Kernel Kernel => Kernel.CreateBuilder()
        .AddAzureOpenAIChatCompletion(
            AzureOpenAi.ChatModelDeployment,
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey)
        .AddAzureOpenAIEmbeddingGenerator(
            "text-embedding-3-small",
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey)
        .Build();

    public IKernelBuilder KernelBuilder => Kernel.CreateBuilder()
        .AddAzureOpenAIChatCompletion(
            AzureOpenAi.ChatModelDeployment,
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey);

    public IChatClient ChatClient => new AzureOpenAIClient(
            new Uri(AzureOpenAi.Endpoint),
            new ApiKeyCredential(AzureOpenAi.ApiKey))
        .GetChatClient(AzureOpenAi.ChatModelDeployment)
        .AsIChatClient();
}