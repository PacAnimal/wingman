namespace Wingman;

public class WingmanSettings
{
    public string? ApiKey { get; set; }
    public AiProviderKind? Provider { get; set; }
    public List<string>? Memories { get; set; }

    // Legacy field — kept so old encrypted settings files still deserialize.
    public string? OpenAiApiKey { get; set; }
    public string? AnthropicApiKey { get; set; }

    public string? KeyForProvider(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenAI => OpenAiApiKey
            ?? (ApiKey != null && (Provider ?? AiProviderKind.OpenAI) == AiProviderKind.OpenAI ? ApiKey : null),
        AiProviderKind.Anthropic => AnthropicApiKey
            ?? (ApiKey != null && Provider == AiProviderKind.Anthropic ? ApiKey : null),
        _ => null
    };

    public string? EffectiveApiKey => KeyForProvider(Provider ?? AiProviderKind.OpenAI);
}
