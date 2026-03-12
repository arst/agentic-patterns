using Microsoft.Extensions.Configuration;

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
}

public class AzureOpenAiSettiings
{
    public string ChatModelDeployment { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}