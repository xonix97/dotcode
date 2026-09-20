using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace DotCode.Providers;

/// <summary>Builds <see cref="IChatClient"/> instances over OpenAI-compatible endpoints.</summary>
public static class ChatClientFactory
{
    public static IChatClient Create(ResolvedEndpoint endpoint, string modelId)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
        var credential = new ApiKeyCredential(endpoint.ApiKey ?? "none");
        var chatClient = new OpenAI.Chat.ChatClient(modelId, credential, options);
        return chatClient.AsIChatClient();
    }
}
