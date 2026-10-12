using System.Security.Cryptography;
using System.Text;

namespace TcBusBot.Core.Chat;

/// <summary>
/// 「放在資料庫裡的金鑰」的保護：用主人自己知道的密語（`LLM_ADMIN_KEY`）加密。
///
/// ── 為什麼需要這一層 ────────────────────────────────────
/// `/ai llm` 讓管理員在 Discord 上換金鑰，而那組金鑰必須**跨重啟**留著
/// （Render 每次重新部署都是新的行程、新的檔案系統，環境變數才活得下來 ——
/// 那正是這個功能要取代的東西）。所以它只能存進**資料庫**（MongoDB／SQLite／文字檔）。
///
/// 可是資料庫跟行程環境是兩個不同的信任邊界：環境變數只有主機主人看得到，
/// 資料庫卻可能會被備份、傾印、或分享出去（例如把 MongoDB 連線字串給別人除錯）。
/// 明文存進去等於「金鑰多了一份副本，而且放在比較容易被看到的地方」。
///
/// 所以：有 `LLM_ADMIN_KEY` 就拿它推導金鑰做 **AES-GCM**（同時保護機密性與完整性），
/// 沒有就明講「沒有加密」（見 <see cref="Protect"/> 的回傳值與 `LlmEndpointStore.Encrypted`）——
/// **不假裝**，因為「以為加密了其實沒有」比「知道沒有加密」危險得多。
///
/// ⚠️ 這不是萬靈丹：能讀資料庫又能讀行程環境的人還是解得開（那個人本來就等於主人）。
///    它擋的是「只有資料庫那一份」的情況。
/// </summary>
public static class LlmSecretProtector
{
    /// <summary>加密過的字首（看到這個才去解密；沒有的話就是明文）。</summary>
    public const string EncryptedPrefix = "enc:v1:";

    /// <summary>明文時刻意加的字首 —— 讓資料庫裡一眼看得出「這一筆沒有保護」。</summary>
    public const string PlainPrefix = "plain:";

    /// <summary>PBKDF2 的固定鹽（不是機密；目的是讓同一組密語在不同機器上推導出一樣的鍵）。</summary>
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("tcbus-llm-endpoint-v1");

    private const int Iterations = 100_000;
    private const int KeySize = 32;   // AES-256
    private const int NonceSize = 12; // AES-GCM 標準
    private const int TagSize = 16;

    /// <summary>這一組密語能不能用來加密（空字串 ＝ 不能）。</summary>
    public static bool CanProtect(string? secret) => !string.IsNullOrWhiteSpace(secret);

    /// <summary>
    /// 加密（沒有密語時回傳 <see cref="PlainPrefix"/> ＋ 明文）。
    ///
    /// 回傳值一定帶著字首，所以**呼叫端不需要另外記「這一筆有沒有加密」**——
    /// 資料庫裡的字串自己說得清楚，而且換了密語之後還讀得出來「這是什麼」。
    /// </summary>
    public static string Protect(string plaintext, string? secret)
    {
        var text = plaintext ?? "";

        if (!CanProtect(secret)) return PlainPrefix + text;

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var bytes = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[bytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(DeriveKey(secret!), TagSize);
        aes.Encrypt(nonce, bytes, cipher, tag);

        // nonce ＋ tag ＋ 密文 一起放進同一個字串（解密時照同樣的順序切開）
        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);

        return EncryptedPrefix + Convert.ToBase64String(packed);
    }

    /// <summary>
    /// 解密。**解不開就回 null**（不是丟例外、也不是回傳垃圾）：
    ///
    /// 最常見的原因是「主人換了 `LLM_ADMIN_KEY`」—— 那時舊的密文就永遠解不開了。
    /// 這時候正確的行為是**回到環境變數的設定並出聲**（見 `LlmEndpointStore`），
    /// 而不是讓 Bot 拿著一串亂碼去打 API（那會變成一個看不出原因的 401）。
    /// </summary>
    public static string? Unprotect(string? stored, string? secret)
    {
        if (string.IsNullOrEmpty(stored)) return null;

        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return stored.StartsWith(PlainPrefix, StringComparison.Ordinal)
                ? stored[PlainPrefix.Length..]
                : stored;   // 沒有字首 = 舊資料或人工寫入的明文，照用

        if (!CanProtect(secret)) return null;   // 有密文但這次沒有密語 → 解不開

        try
        {
            var packed = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);

            if (packed.Length < NonceSize + TagSize) return null;

            var nonce = packed.AsSpan(0, NonceSize);
            var tag = packed.AsSpan(NonceSize, TagSize);
            var cipher = packed.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(DeriveKey(secret!), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            // 密語不對、內容被改過、格式壞掉 —— 全部都是「讀不出來」，一律回 null
            return null;
        }
    }

    /// <summary>這一筆字串是不是加密過的（給 UI 說「有加密保存」用）。</summary>
    public static bool IsEncrypted(string? stored)
        => stored?.StartsWith(EncryptedPrefix, StringComparison.Ordinal) == true;

    /// <summary>密語 → 32 byte 金鑰（PBKDF2-SHA256）。</summary>
    private static byte[] DeriveKey(string secret)
        => Rfc2898DeriveBytes.Pbkdf2(secret, Salt, Iterations, HashAlgorithmName.SHA256, KeySize);
}
