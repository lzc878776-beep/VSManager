using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.AI;
using AIMessage = Microsoft.Extensions.AI.ChatMessage;
using AIRole = Microsoft.Extensions.AI.ChatRole;

namespace VSManager
{
    public sealed partial class AgentService
    {
        /// <summary>单条用户消息最多直接发给模型查看的图片数。/ Images per user message sent to the model for viewing.</summary>
        internal const int MaxModelImages = 4;

        /// <summary>主流视觉接口通用的图片格式（BMP 多数不支持，只作为元数据列出）。/ Image formats widely accepted by vision APIs (BMP is usually not, so it is listed as metadata only).</summary>
        private static readonly string[] ModelImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        /// <summary>本会话中接口已拒绝图片的「地址|模型」，之后改为纯文字。/ "endpoint|model" whose API rejected images this session; text only afterwards.</summary>
        private string _visionRejected;

        internal static string ImageMediaType(string ext)
        {
            switch (AttachmentPolicy.NormalizeExt(ext))
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return null;
            }
        }

        /// <summary>
        /// 当前模型是否接收用户消息里的图片：已知纯文字模型或本会话已被接口拒绝时为否。
        /// Whether the current model receives images in user messages: no for known text-only models or after the API rejected them this session.
        /// </summary>
        private bool ModelAcceptsImages()
        {
            var s = _settings();
            string endpoint = (s.AgentEndpoint ?? "").Trim(), model = (s.AgentModel ?? "").Trim();
            return !IsKnownTextOnlyModel(endpoint, model) && _visionRejected != endpoint + "|" + model;
        }

        private static byte[] ReadAttachmentBytes(AttachmentRef a)
        {
            string path = AttachmentStore.FullPath(a);
            if (path == null) return null;
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// 生成发给模型的用户消息：支持看图时把图片（最多 <see cref="MaxModelImages"/> 张、单张不超过 <see cref="ChatImage.MaxBytes"/>）作为图片内容附上，其余仍只列元数据。
        /// Builds the user message for the model: with vision, images (up to <see cref="MaxModelImages"/>, each within <see cref="ChatImage.MaxBytes"/>) are attached as image content; the rest stay metadata only.
        /// </summary>
        internal static AIMessage UserModelMessage(string text, IReadOnlyList<AttachmentRef> files, bool vision,
            Func<AttachmentRef, byte[]> readBytes = null, Func<AttachmentRef, int, string> readText = null)
        {
            if (files == null || files.Count == 0) return new AIMessage(AIRole.User, text);
            readBytes = readBytes ?? ReadAttachmentBytes;
            var images = new List<DataContent>();
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (vision)
            {
                foreach (var a in files.Where(f => f.IsImage && ModelImageExtensions.Contains(AttachmentPolicy.NormalizeExt(f.Ext ?? f.Name))))
                {
                    if (images.Count >= MaxModelImages) break;
                    byte[] data = readBytes(a);
                    if (data == null || data.Length == 0 || data.Length > ChatImage.MaxBytes) continue;
                    images.Add(new DataContent(data, ImageMediaType(a.Ext ?? a.Name)));
                    shown.Add(a.Id);
                }
            }
            string body = ModelMessage(text, files, readText, shown);
            if (images.Count == 0) return new AIMessage(AIRole.User, body);
            var contents = new List<AIContent> { new TextContent(body) };
            contents.AddRange(images);
            return new AIMessage(AIRole.User, contents);
        }

        /// <summary>
        /// 只保留最近一条带图消息的图片，更早的替换为文字说明，避免每轮重复上传。keepLatest 为 false 时全部替换（接口拒绝图片后）。
        /// Keeps images only in the latest image-bearing message and replaces older ones with a note so they are not re-uploaded every round; with keepLatest false all are replaced (after the API rejected images).
        /// </summary>
        internal static void PruneHistoryImages(List<AIMessage> history, bool keepLatest)
        {
            int latest = keepLatest ? history.FindLastIndex(m => m.Contents.OfType<DataContent>().Any()) : -1;
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (i == latest || !m.Contents.OfType<DataContent>().Any()) continue;
                int n = m.Contents.OfType<DataContent>().Count();
                var kept = m.Contents.Where(c => !(c is DataContent)).ToList();
                kept.Add(new TextContent("\n（此消息的 " + n + " 张图片已在当时查看，不再重复发送 / " + n + " image(s) of this message were viewed earlier and are not resent）"));
                var replaced = new AIMessage(m.Role, kept) { AuthorName = m.AuthorName, MessageId = m.MessageId };
                SetScope(replaced, ScopeOf(m));
                history[i] = replaced;
            }
        }

        /// <summary>
        /// 请求失败且本轮带了图片、错误表明不支持图片时：本会话改为纯文字并移除历史中的图片，返回给用户的提示；否则返回 null。
        /// When a request that carried images fails because images are unsupported: switch to text only for this session, drop images from history and return a notice; otherwise null.
        /// </summary>
        private string HandleVisionRejected(Exception ex, bool sentImages)
        {
            if (!sentImages || !LooksLikeVisionUnsupported(ex)) return null;
            var s = _settings();
            _visionRejected = (s.AgentEndpoint ?? "").Trim() + "|" + (s.AgentModel ?? "").Trim();
            PruneHistoryImages(_history, false);
            return "当前模型不接受图片，本次会话已改为只发送文字，请重新发送 / The model does not accept images; this session now sends text only, please resend";
        }
    }
}
