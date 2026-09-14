using System.Collections.Concurrent;
using System.Text;

namespace TcBusBot.Mobile;

/// <summary>
/// 手機上的「主控台」。
///
/// 既有的 Bot 到處都在 <c>Console.WriteLine</c>（啟動橫幅、註冊指令、輪詢、錯誤診斷…），
/// 那些訊息在手機上原本會消失。這裡把 Console 導到：
///   1. 畫面上的日誌區（使用者看得到「Bot 已上線」「輪詢查了幾個站」）
///   2. logcat（用 adb logcat 也能追）
///
/// **不改 Bot 的任何一行輸出程式碼** —— 這是「能力一致」最省事的做法。
/// </summary>
public static class BotConsole
{
    private const int MaxLines = 400;
    private static readonly ConcurrentQueue<string> Lines = new();
    private static readonly StringBuilder Pending = new();

    public static event Action? Updated;

    public static void Install()
    {
        Console.SetOut(new LineWriter());
        Console.SetError(new LineWriter());
    }

    public static string Dump()
    {
        lock (Pending) return string.Join("\n", Lines);
    }

    public static void Clear()
    {
        while (Lines.TryDequeue(out _)) { }
        lock (Pending) Pending.Clear();
        Updated?.Invoke();
    }

    public static void Write(string text)
    {
        // Console.WriteLine 的內容可能是「半行」的（Write 後才 WriteLine），
        // 這裡先累積到換行才輸出，畫面才不會出現破碎的行。
        List<string>? completed = null;

        lock (Pending)
        {
            Pending.Append(text);

            while (true)
            {
                var current = Pending.ToString();
                var newline = current.IndexOf('\n');
                if (newline < 0) break;

                var line = current[..newline].TrimEnd('\r');
                Pending.Remove(0, newline + 1);

                completed ??= new List<string>();
                completed.Add(line);
            }
        }

        if (completed is null) return;

        foreach (var line in completed)
        {
            Lines.Enqueue(line);
            Android.Util.Log.Info("TcBusBot", line);
        }

        while (Lines.Count > MaxLines) Lines.TryDequeue(out _);
        Updated?.Invoke();
    }

    private sealed class LineWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => BotConsole.Write(value.ToString());
        public override void Write(string? value) { if (value is not null) BotConsole.Write(value); }
        public override void WriteLine(string? value) => BotConsole.Write((value ?? "") + "\n");
        public override void WriteLine() => BotConsole.Write("\n");
    }
}
