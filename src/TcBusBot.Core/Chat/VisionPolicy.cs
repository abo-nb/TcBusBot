namespace TcBusBot.Core.Chat;

/// <summary>圖片從哪裡來（log 與測試用；送給模型時只是網址）。</summary>
public enum ImageSource
{
    /// <summary>訊息附件的圖片檔。</summary>
    Attachment,

    /// <summary>Discord 貼圖（stickers）。</summary>
    Sticker,

    /// <summary>嵌入（embed）裡的圖片。</summary>
    Embed
}

/// <summary>
/// 一張要給模型看的圖片。
///
/// 為什麼存**網址**而不是 base64：
///   * Discord 的附件網址本身就是公開的 https 連結（帶簽章、會過期），
///     模型那邊自己去抓就好 —— 不必把圖片抓下來、編碼、塞進請求（48 MiB 上限）。
///   * 我們只要傳一個幾百字元的字串，頻寬與記憶體都不必動。
/// ⚠️ 網址會過期（Discord 簽章通常 24 小時），所以**只用在當下那一則訊息**，
///    不會拿舊的網址去重問（歷史訊息裡的圖片一律只留文字記號）。
/// </summary>
public sealed record ImageRef(
    string Url,
    ImageSource Source,
    string? FileName = null,
    long Bytes = 0)
{
    /// <summary>給 log 用的一行說明（不要把整串簽章網址印出來）。</summary>
    public string Describe()
        => $"{Source switch { ImageSource.Attachment => "附件", ImageSource.Sticker => "貼圖", _ => "嵌入" }}" +
           $"{(FileName is null ? "" : $"「{FileName}」")}（{Bytes / 1024.0:0} KB）";
}

/// <summary>
/// 「這一則訊息要不要把圖片送給模型看」的規則。
///
/// ── 為什麼需要一條規則，而不是看到圖就送 ──────────────────
/// 1. **成本**：一張圖最多 1024 tokens（官方文件寫的換算方式），
///    而這支 Bot 一天可能被丟幾十張圖。看到就送，額度會在不知不覺中燒完。
/// 2. **隱私**：偷聽到的訊息本來就只是「聽」，把別人隨手貼的照片送去外部模型
///    是另一件事 —— 使用者要的是「**我指定**才看」。
/// 3. **雜訊**：群組裡大量貼圖跟對話無關，硬塞給模型只會讓回答變差。
///
/// 所以規則很簡單：**只有「明確對 Bot 說話」的那一則**（@ 它、或回覆它）才可能帶圖；
/// 偷聽到的訊息一律只有文字。而且：
///   * 回覆某張圖的訊息時，**被回覆的那一則的圖片**也算（「這張圖是什麼？」很自然）
///   * 一次最多 <c>maxImages</c> 張（預設 2），多的只留文字記號
///   * 有圖但沒開圖片理解（`LLM_VISION=false`）時，會留一句「（有 N 張圖片，主機沒開圖片理解）」
///     讓模型知道自己看不到，而不是憑空編造
/// </summary>
public static class VisionPolicy
{
    /// <summary>
    /// 挑出這次要送的圖片：先當前訊息的、再被回覆訊息的，最多 <paramref name="maxImages"/> 張。
    /// </summary>
    public static IReadOnlyList<ImageRef> Select(
        IReadOnlyList<ImageRef> current,
        IReadOnlyList<ImageRef> replied,
        bool addressed,
        int maxImages)
    {
        if (!addressed || maxImages <= 0) return [];

        var picked = new List<ImageRef>(maxImages);

        foreach (var image in current.Concat(replied))
        {
            if (string.IsNullOrWhiteSpace(image.Url)) continue;
            if (picked.Any(p => string.Equals(p.Url, image.Url, StringComparison.Ordinal))) continue;

            picked.Add(image);
            if (picked.Count >= maxImages) break;
        }

        return picked;
    }

    /// <summary>這一則有圖片時的提示文字（模型看得到「有圖」這件事）。</summary>
    public static string Note(int imageCount, int sentCount, bool visionEnabled)
    {
        if (imageCount == 0) return "";

        if (!visionEnabled)
            return $"（這一則有 {imageCount} 張圖片，但主機沒有開啟圖片理解，你看不到內容）";

        if (sentCount == 0)
            return $"（這一則有 {imageCount} 張圖片）";

        return sentCount < imageCount
            ? $"（附上 {sentCount} 張圖片，另外 {imageCount - sentCount} 張沒有附上）"
            : $"（附上 {sentCount} 張圖片）";
    }
}
