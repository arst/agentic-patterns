namespace Shared;

public class AzureOpenAISettings
{
    public string ChatModelDeployment { get; set; } = string.Empty;
    public string EmbeddingModelDeployment { get; set; } = "text-embedding-3-small";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}