using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;

#pragma warning disable SKEXP0010

namespace Shared;

public static class Settings
{
    private static readonly IConfigurationRoot Root = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", false)
        .AddUserSecrets<AzureOpenAiSettiings>(true)
        .AddEnvironmentVariables()
        .Build();

    public static Mem0ApiSettings Mem0ApiSettings => field ??=
        Root.GetSection("Mem0").Get<Mem0ApiSettings>() ?? new Mem0ApiSettings();

    public static AzureOpenAiSettiings AzureOpenAi => field ??=
        Root.GetSection("AzureOpenAI").Get<AzureOpenAiSettiings>()
        ?? new AzureOpenAiSettiings();

    public static Kernel Kernel => field ??= Kernel.CreateBuilder()
        .AddAzureOpenAIChatCompletion(
            AzureOpenAi.ChatModelDeployment,
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey)
        .AddAzureOpenAIEmbeddingGenerator(
            "text-embedding-3-small",
            AzureOpenAi.Endpoint,
            AzureOpenAi.ApiKey)
        .Build();

    public static IChatClient ChatClient => field ??= new AzureOpenAIClient(
            new Uri(AzureOpenAi.Endpoint),
            new ApiKeyCredential(AzureOpenAi.ApiKey))
        .GetChatClient(AzureOpenAi.ChatModelDeployment)
        .AsIChatClient();

    public static IKernelBuilder CreateKernelBuilder()
    {
        return Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                AzureOpenAi.ChatModelDeployment,
                AzureOpenAi.Endpoint,
                AzureOpenAi.ApiKey);
    }
}