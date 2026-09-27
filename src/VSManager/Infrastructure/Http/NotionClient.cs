using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VSManager
{
    /// <summary>
    /// 最小 Notion REST 客户端：只读数据库记录与页面块，只更新指定的单个属性，从不修改标题、正文或页面位置。
    /// Minimal Notion REST client: reads database records and page blocks, and updates one named property only;
    /// never touches titles, bodies or page locations.
    /// </summary>
    internal interface INotionClient
    {
        Task<List<object>> QueryDatabaseAsync(string databaseId, CancellationToken ct);
        Task<List<object>> ReadBlocksAsync(string pageId, CancellationToken ct);
        Task UpdateStatusAsync(string pageId, string property, string propertyType, string value, CancellationToken ct);
    }

    internal sealed class NotionClient : INotionClient
    {
        private const string BaseUrl = "https://api.notion.com/v1/";
        private const string ApiVersion = "2022-06-28";
        private const int MaxPages = 20, MaxRetries = 3, TimeoutMs = 30000, MaxStatusText = 1900;
        private readonly Func<string> _token;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public NotionClient(Func<string> token)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
        }

        public async Task<List<object>> QueryDatabaseAsync(string databaseId, CancellationToken ct)
        {
            var all = new List<object>();
            string cursor = null;
            for (int i = 0; i < MaxPages; i++)
            {
                var body = new Dictionary<string, object> { ["page_size"] = 100 };
                if (cursor != null) body["start_cursor"] = cursor;
                var r = await SendAsync("POST", "databases/" + databaseId + "/query", body, ct).ConfigureAwait(false);
                all.AddRange(NotionPlanParser.Array(NotionPlanParser.Get(r, "results")));
                if (!(NotionPlanParser.Get(r, "has_more") is bool more && more)) return all;
                cursor = NotionPlanParser.Get(r, "next_cursor") as string;
            }
            throw new IOException("计划记录过多（超过 2000 条）/ Plan has too many records (over 2000)");
        }

        public async Task<List<object>> ReadBlocksAsync(string pageId, CancellationToken ct)
        {
            var all = new List<object>();
            string cursor = null;
            for (int i = 0; i < MaxPages; i++)
            {
                string path = "blocks/" + pageId + "/children?page_size=100" + (cursor != null ? "&start_cursor=" + Uri.EscapeDataString(cursor) : "");
                var r = await SendAsync("GET", path, null, ct).ConfigureAwait(false);
                all.AddRange(NotionPlanParser.Array(NotionPlanParser.Get(r, "results")));
                if (!(NotionPlanParser.Get(r, "has_more") is bool more && more)) break;
                cursor = NotionPlanParser.Get(r, "next_cursor") as string;
            }
            return all;
        }

        public Task UpdateStatusAsync(string pageId, string property, string propertyType, string value, CancellationToken ct)
        {
            string v = value.Length > MaxStatusText ? value.Substring(0, MaxStatusText) : value;
            object prop = propertyType == "select"
                ? new Dictionary<string, object> { ["select"] = new Dictionary<string, object> { ["name"] = v.Length > 100 ? v.Substring(0, 100) : v } }
                : (object)new Dictionary<string, object> { ["rich_text"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = new Dictionary<string, object> { ["content"] = v } } } };
            var body = new Dictionary<string, object> { ["properties"] = new Dictionary<string, object> { [property] = prop } };
            return SendAsync("PATCH", "pages/" + pageId, body, ct);
        }

        private async Task<object> SendAsync(string method, string path, object body, CancellationToken ct)
        {
            string token = _token();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("未配置 Notion 集成 Token（设置或环境变量 " + AppSettings.NotionTokenEnvVar + "）/ Notion integration token not configured (settings or " + AppSettings.NotionTokenEnvVar + ")");
            string payload = body == null ? null : _json.Serialize(body);
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var (status, text, retryAfter) = await Task.Run(() => Send(method, path, payload, token), ct).ConfigureAwait(false);
                if (status >= 200 && status < 300) return _json.DeserializeObject(text);
                if ((status == 429 || status >= 500) && attempt < MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryAfter > 0 ? Math.Min(retryAfter, 30) : 1 << attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw new IOException(Describe(status, text));
            }
        }

        private static (int Status, string Text, int RetryAfter) Send(string method, string path, string payload, string token)
        {
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            req.Method = method;
            req.Timeout = req.ReadWriteTimeout = TimeoutMs;
            req.Headers["Authorization"] = "Bearer " + token.Trim();
            req.Headers["Notion-Version"] = ApiVersion;
            if (payload != null)
            {
                req.ContentType = "application/json; charset=utf-8";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return ((int)resp.StatusCode, Read(resp), 0);
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse resp)
            {
                using (resp)
                {
                    int.TryParse(resp.Headers["Retry-After"], out int retry);
                    return ((int)resp.StatusCode, Read(resp), retry);
                }
            }
        }

        private static string Read(WebResponse resp)
        {
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
        }

        private string Describe(int status, string text)
        {
            string message = null;
            try { message = NotionPlanParser.Get(_json.DeserializeObject(text), "message") as string; } catch (ArgumentException) { }
            string hint = status == 401 ? "Token 无效 / Invalid token"
                : status == 404 ? "找不到数据库或页面，确认已把集成添加到该数据库 / Not found; share the database with the integration"
                : status == 400 ? "请求无效，检查状态字段名称与类型 / Bad request; check the status property name and type"
                : "Notion 请求失败 / Notion request failed";
            return hint + " (HTTP " + status + ")" + (string.IsNullOrEmpty(message) ? "" : "：" + message);
        }
    }
}
