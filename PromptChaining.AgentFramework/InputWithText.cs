namespace PromptChaining.AgentFramework;

internal record ExtractedEntities(string[] People, string[] Orgs, string[] Topics);

internal record InputWithText(ExtractedEntities Entities, string OriginalText);
