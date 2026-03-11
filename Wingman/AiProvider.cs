using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Wingman;

public enum AiProviderKind { OpenAi, Anthropic }

public sealed class AiProvider
{
    public AiProviderKind Kind { get; }
    public string ChatModel { get; }
    public string GuardModel { get; }
    public bool SupportsWebSearch { get; }

    private AiProvider(AiProviderKind kind, string chatModel, string guardModel, bool supportsWebSearch)
    {
        Kind = kind;
        ChatModel = chatModel;
        GuardModel = guardModel;
        SupportsWebSearch = supportsWebSearch;
    }

    public static AiProvider Detect(string apiKey) =>
        apiKey.StartsWith("sk-ant-", StringComparison.Ordinal)
            ? new AiProvider(AiProviderKind.Anthropic, "claude-sonnet-4-6", "claude-haiku-4-5-20251001", supportsWebSearch: false)
            : new AiProvider(AiProviderKind.OpenAi, "gpt-5.2", "gpt-5-mini", supportsWebSearch: true);

    public IChatClient CreateChatClient(string apiKey) => Kind switch
    {
        AiProviderKind.OpenAi => new OpenAIClient(apiKey)
            .GetResponsesClient()
            .AsIChatClient(ChatModel)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build(),
        AiProviderKind.Anthropic => new AnthropicClient { ApiKey = apiKey }
            .AsIChatClient(ChatModel)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(apiKey), $"Unsupported provider kind: {Kind}")
    };

    public IChatClient CreateGuardClient(string apiKey) => Kind switch
    {
        AiProviderKind.OpenAi => new OpenAIClient(apiKey)
            .GetResponsesClient()
            .AsIChatClient(GuardModel),
        AiProviderKind.Anthropic => new AnthropicClient { ApiKey = apiKey }
            .AsIChatClient(GuardModel),
        _ => throw new ArgumentOutOfRangeException(nameof(apiKey), $"Unsupported provider kind: {Kind}")
    };
}
