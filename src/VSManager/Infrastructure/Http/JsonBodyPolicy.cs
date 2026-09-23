using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VSManager
{
    /// <summary>在请求发出前修改 JSON 请求体（用于添加 OpenAI SDK 不支持的厂商扩展参数）。</summary>
    internal sealed class JsonBodyPolicy : PipelinePolicy
    {
        private readonly Action<JsonObject> _edit;

        public JsonBodyPolicy(Action<JsonObject> edit) => _edit = edit;

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Patch(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Patch(message);
            return ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void Patch(PipelineMessage message)
        {
            var content = message.Request.Content;
            if (content == null) return;
            try
            {
                using (var ms = new MemoryStream())
                {
                    content.WriteTo(ms, default);
                    if (!(JsonNode.Parse(ms.ToArray()) is JsonObject body)) return;
                    _edit(body);
                    message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
                }
            }
            catch { }
        }
    }
}
