using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TcBusBot.Core.Hosting;

/// <summary>
/// 極輕量的 HTTP 健康檢查端點（給 Render 這類 PaaS 用）。
///
/// **為什麼需要**：Render 的免費層只提供「Web Service」——服務必須監聽一個 HTTP 埠。
/// 純 Console 程式在那邊會被判定為沒有開埠、部署失敗。
///
/// **為什麼不用 ASP.NET Core（`Microsoft.NET.Sdk.Web` + `WebApplication`）**：
///
/// | 平台 | ASP.NET Core 可用嗎 |
/// | --- | --- |
/// | Render（Linux 容器） | ✅ |
/// | Windows／Linux 桌面版 | ✅ |
/// | **Termux（手機控制台版）** | ❌ Termux 只有 `dotnet-runtime`，沒有 ASP.NET Core 執行階段 |
/// | （Android APK，目前已擱置） | ❌ 沒有 `Microsoft.AspNetCore.App` 框架參考 |
///
/// 而我們只需要「聽一個埠、回 200」，所以自己寫最省：零套件、零框架參考。
///
/// **為什麼不用 `HttpListener`**：它在 Windows 上需要 HTTP.SYS 的 URL ACL
/// （非管理員無法綁定 `http://+:port/`，連 `http://localhost:port/` 也不行），
/// 變成「Render 上可以、本機跑不起來」—— 那就沒辦法在本機驗證了。
/// 用 <see cref="TcpListener"/> 自己回一段 HTTP/1.1 就沒有這個限制，也不用任何權限。
///
/// 端點：
/// <code>
///   GET /        → 200 "TcBusBot is running!"（Render 健康檢查（healthCheckPath: /health）
///   GET /health  → 200 JSON 狀態（訂閱數、快取筆數、上次輪詢…），瀏覽器直接看得懂
///   其他          → 404
/// </code>
/// </summary>
public sealed class HealthEndpoint : IDisposable
{
    private readonly int _port;
    private readonly Func<string> _statusJson;
    private readonly TcpListener _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>單一請求最多等多久（避免慢速連線把執行緒佔住）。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public HealthEndpoint(int port, Func<string> statusJson)
    {
        _port = port;
        _statusJson = statusJson;

        // 監聽所有介面：容器／Render 需要從外部連進來
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 實際綁定的埠。建構時傳 0 表示「讓作業系統挑一個空閒埠」，
    /// 綁定之後用這個屬性查（離線測試就是靠這個，不必猜哪個埠沒被占用）。
    /// </summary>
    public int Port => (_listener.LocalEndpoint as IPEndPoint)?.Port ?? _port;

    /// <summary>成功的請求數（給日誌與診斷用）。</summary>
    public int RequestCount { get; private set; }

    /// <summary>開始監聽；<paramref name="message"/> 說明結果。</summary>
    public bool Start(out string message)
    {
        try
        {
            _listener.Start();

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

            IsRunning = true;
            message = $"http://0.0.0.0:{Port}/（GET / 與 /health）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"無法在埠 {_port} 上監聽（{ex.GetType().Name}: {ex.Message}）。" +
                      "Bot 會繼續運作，但沒有健康檢查端點。";
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Stop() 之後的正常結束
                return;
            }

            // 每個連線各自處理，不要卡住接受迴圈
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = (int)RequestTimeout.TotalMilliseconds;
                client.SendTimeout = (int)RequestTimeout.TotalMilliseconds;

                using var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(RequestTimeout);

                var head = await ReadRequestHeadAsync(stream, timeout.Token).ConfigureAwait(false);
                if (head is null) return;

                var (method, path) = ParseRequestLine(head);

                var (status, reason, contentType, body) = Route(method, path);
                if (method == "HEAD") body = "";

                await WriteResponseAsync(stream, status, reason, contentType, body, timeout.Token)
                    .ConfigureAwait(false);

                RequestCount++;
            }
        }
        catch (Exception)
        {
            // 客戶端斷線、格式不對…一律忽略：健康檢查端點不該影響 Bot
        }
    }

    private (int Status, string Reason, string ContentType, string Body) Route(string method, string path)
    {
        if (method is not ("GET" or "HEAD"))
            return (405, "Method Not Allowed", "text/plain; charset=utf-8", "method not allowed");

        return path switch
        {
            "/" => (200, "OK", "text/plain; charset=utf-8", "TcBusBot is running!"),
            "/health" => (200, "OK", "application/json; charset=utf-8", _statusJson()),
            _ => (404, "Not Found", "text/plain; charset=utf-8", "not found")
        };
    }

    /// <summary>讀到第一個空行（或讀滿上限）為止；回傳請求的 head 文字。</summary>
    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                                    .ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);

            // 只需要第一行（方法 + 路徑）；標頭不重要，但讀到空行才算完整的請求
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) ||
                text.Contains("\n\n", StringComparison.Ordinal))
                return text;
        }

        // 只讀到第一行也勉強可用（有些工具送得很精簡）
        var partial = Encoding.ASCII.GetString(buffer, 0, total);
        return partial.Contains('\n') || partial.Length > 0 ? partial : null;
    }

    /// <summary>從 "GET /health HTTP/1.1" 取出方法與路徑。</summary>
    private static (string Method, string Path) ParseRequestLine(string head)
    {
        var line = head.Split('\n', 2)[0].Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2) return ("", "/");

        var method = parts[0].ToUpperInvariant();
        var path = parts[1];

        // 去掉查詢字串與尾斜線（/health?x=1 → /health）
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        path = path.TrimEnd('/');
        return (method, path.Length == 0 ? "/" : path);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string reason, string contentType, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        var header = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {bytes.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n")     // 不支援 keep-alive：每次連線回完就關，最單純
            .Append("\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
        if (bytes.Length > 0)
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        IsRunning = false;

        try { _cts?.Cancel(); } catch { /* 已取消 */ }
        try { _listener.Stop(); } catch { /* 已停止 */ }

        _cts?.Dispose();
    }
}
