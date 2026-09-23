using System;
using Microsoft.Extensions.AI;

namespace VSManager
{
    /// <summary>
    /// AI 接口客户端工厂：按接口地址、模型与 Key 创建底层聊天客户端（不含工具调用）。单元测试可替换为模拟实现。
    /// Factory of AI chat clients: creates the underlying chat client for an endpoint, model and key (without tool
    /// invocation). Unit tests can replace it with a fake.
    /// </summary>
    public interface IAiClientFactory
    {
        IChatClient Create(Uri endpoint, string model, string apiKey);
    }

    /// <summary>
    /// OpenAI 兼容接口（DeepSeek、火山方舟、本地模型等）的默认实现。
    /// Default implementation for OpenAI-compatible endpoints (DeepSeek, Volcano Ark, local models, ...).
    /// </summary>
    public sealed class OpenAiClientFactory : IAiClientFactory
    {
        public static readonly OpenAiClientFactory Instance = new OpenAiClientFactory();

        public IChatClient Create(Uri endpoint, string model, string apiKey)
        {
            var options = new OpenAI.OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = TimeSpan.FromMinutes(5) };
            // DeepSeek 默认开启思考模式，带工具调用时要求回传 reasoning_content（OpenAI SDK 不支持），因此关闭思考
            // DeepSeek enables thinking by default and then requires reasoning_content to be sent back with tool calls
            // (unsupported by the OpenAI SDK), so thinking is disabled
            if (AgentPresets.IsDeepSeek(endpoint.ToString()) || model.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)
                options.AddPolicy(new JsonBodyPolicy(o => { if (o["messages"] != null && o["thinking"] == null) o["thinking"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "disabled" }; }),
                    System.ClientModel.Primitives.PipelinePosition.PerCall);
            return new OpenAI.Chat.ChatClient(model, new System.ClientModel.ApiKeyCredential(apiKey), options).AsIChatClient();
        }
    }
}
