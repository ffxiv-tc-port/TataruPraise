using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TataruPraise.Core;

/// <summary>pool.json 裡的一句誇獎。</summary>
public sealed class PraiseLine
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;

    /// <summary>相對於外掛設定資料夾的語音快取路徑，例如 <c>cache/9f3a1c….wav</c>；還沒合成就是空字串。</summary>
    [JsonPropertyName("wav")] public string Wav { get; set; } = string.Empty;
}

/// <summary>
/// 誇獎池：<c>pool.json</c> 的讀寫、內建種子、挑句。
/// </summary>
/// <remarks>
/// 檔案放在 <c>Svc.PluginInterface.GetPluginConfigDirectory()</c> 底下，語音快取在其中的 <c>cache/</c>。
/// <para>
/// 🔴 所有公開成員都在同一把鎖底下：擴充池／預合成是背景 Task，而 UI 每幀都在讀同一份資料。
/// </para>
/// <para>
/// 📌 讀檔用的字典<b>保留不認得的鍵</b>（規格書列過「成就」「採集製作大成功」「連續登入」等
/// 這一版還沒有觸發來源的情境）——使用者自己加的東西不會在下一次存檔時被吃掉。
/// </para>
/// </remarks>
public sealed class PraisePool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 不轉義 CJK：pool.json 是使用者會自己打開來看／編輯的檔案。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly Random random = new();

    private static readonly TimeSpan AvailabilityCacheDuration = TimeSpan.FromSeconds(2);
    private readonly object availabilityGate = new();
    private DateTime availabilityCheckedUtc = DateTime.MinValue;
    private bool availabilityCache;
    private readonly string dataDir;
    private readonly string poolPath;
    private readonly string cacheDir;

    private Dictionary<string, List<PraiseLine>> pool = [];

    public PraisePool()
    {
        dataDir = Svc.PluginInterface.GetPluginConfigDirectory();
        poolPath = Path.Combine(dataDir, "pool.json");
        cacheDir = Path.Combine(dataDir, "cache");
    }

    public string PoolPath => poolPath;

    public string CacheDirectory => cacheDir;

    /// <summary>句子 → 快取檔名（sha1 十六進位小寫 + .wav）。</summary>
    public static string CacheFileName(string text)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".wav";
    }

    /// <summary>某句話的語音快取絕對路徑（不保證存在）。</summary>
    public string CachePathFor(string text) => Path.Combine(cacheDir, CacheFileName(text));

    /// <summary>讀檔；讀不到或壞掉就當成空池（不擲例外，外掛照樣載入）。</summary>
    public void Load()
    {
        Dictionary<string, List<PraiseLine>>? loaded = null;
        try
        {
            if (File.Exists(poolPath))
            {
                // utf-8-sig：手動編輯過的檔常常帶 BOM，JsonSerializer 對 BOM 會直接擲例外。
                var json = File.ReadAllText(poolPath, Encoding.UTF8);
                loaded = JsonSerializer.Deserialize<Dictionary<string, List<PraiseLine>>>(json, JsonOpts);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 讀取誇獎池失敗，改用空池：{ex.Message}");
        }

        lock (gate)
        {
            pool = loaded ?? [];
            foreach (var key in PraiseCategory.All)
                pool.TryAdd(key, []);
        }
    }

    /// <summary>存檔。失敗只寫 Information，不擲例外。</summary>
    public void Save()
    {
        string json;
        lock (gate)
        {
            json = JsonSerializer.Serialize(pool, JsonOpts);
        }

        try
        {
            Directory.CreateDirectory(dataDir);
            // 先寫暫存檔再換名：中途出錯不會把既有的池截成 0 bytes。
            var tmp = poolPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, poolPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 寫入誇獎池失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 池是空的時候灌進內建預設句。
    /// </summary>
    /// <remarks>
    /// 🔴 判準是「<b>整個池</b>一句都沒有」，不是「這個情境沒有句子」——後者會在使用者刻意清空某一類之後
    /// 每次啟動又偷偷長回來。回傳有沒有真的灌。
    /// </remarks>
    public bool SeedIfEmpty()
    {
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                if (list.Count > 0) return false;
            }

            foreach (var (category, lines) in DefaultPool.Lines)
            {
                if (!pool.TryGetValue(category, out var target))
                {
                    target = [];
                    pool[category] = target;
                }

                foreach (var text in lines)
                    target.Add(new PraiseLine { Text = text });
            }
        }

        Save();
        return true;
    }

    /// <summary>加句子進某個情境；已經有一模一樣的句子就跳過。回傳實際新增幾句。</summary>
    public int AddLines(string category, IEnumerable<string> texts) => AddLines(category, texts, out _);

    /// <summary>
    /// 加句子進某個情境；已經有一模一樣的句子就跳過。回傳實際新增幾句，
    /// <paramref name="duplicates"/> 回傳因為重複而沒有入池的句數。
    /// </summary>
    /// <remarks>
    /// 📌 重複的判準是<b>同一個情境裡</b>的完全相同字串（頭尾空白與引號已由
    /// <see cref="PraiseText.Normalize"/> 剝掉）。跨情境的相同句子刻意不擋——
    /// 同一句話在「升等」與「登入」都合用是正常的，而且語音快取是用句子雜湊命名的，不會多合成一次。
    /// </remarks>
    public int AddLines(string category, IEnumerable<string> texts, out int duplicates)
    {
        var added = 0;
        var skipped = 0;
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list))
            {
                list = [];
                pool[category] = list;
            }

            var seen = new HashSet<string>();
            foreach (var existing in list) seen.Add(existing.Text);

            foreach (var text in texts)
            {
                var trimmed = PraiseText.Normalize(text);
                if (trimmed.Length == 0) continue;
                if (!seen.Add(trimmed))
                {
                    skipped++;
                    continue;
                }

                list.Add(new PraiseLine { Text = trimmed });
                added++;
            }
        }

        duplicates = skipped;
        if (added > 0) Save();
        return added;
    }

    /// <summary>整池有幾句超過 <paramref name="maxLength"/> 字（不含空白）。</summary>
    public int CountLongerThan(int maxLength)
    {
        lock (gate)
        {
            var n = 0;
            foreach (var list in pool.Values)
            {
                foreach (var line in list)
                {
                    if (PraiseText.CountChars(line.Text) > maxLength) n++;
                }
            }

            return n;
        }
    }

    /// <summary>
    /// 把整池裡超過 <paramref name="maxLength"/> 字的句子刪掉，連同它們的語音快取。
    /// 回傳刪掉幾句，<paramref name="deletedWavs"/> 回傳實際刪掉幾個 WAV 檔。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>不可回復</b>的操作，而且動到的是使用者自己的資料——只能由使用者在設定視窗按按鈕觸發，
    /// 絕不可以在載入時、或改滑桿時自動跑。
    /// <para>
    /// 🔴 刪 WAV 之前要先確認整池真的沒有那句話了：同一句可以同時掛在兩個情境底下，而快取檔名是
    /// 句子的雜湊——只按「我剛刪了這筆」就去刪檔，會把另一個情境還在用的語音一起刪掉（而且是靜默的，
    /// 只有到播不出聲的時候才發現）。
    /// </para>
    /// </remarks>
    public int RemoveLongerThan(int maxLength, out int deletedWavs)
    {
        var removedTexts = new HashSet<string>();
        var removedCount = 0;
        var remaining = new HashSet<string>();

        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (PraiseText.CountChars(list[i].Text) <= maxLength) continue;
                    removedTexts.Add(list[i].Text);
                    list.RemoveAt(i);
                    removedCount++;
                }
            }

            foreach (var list in pool.Values)
            {
                foreach (var line in list) remaining.Add(line.Text);
            }
        }

        deletedWavs = 0;
        if (removedCount == 0) return 0;

        Save();

        foreach (var text in removedTexts)
        {
            if (remaining.Contains(text)) continue;

            var path = CachePathFor(text);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedWavs++;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 刪除語音快取失敗（{path}）：{ex.Message}");
            }
        }

        Svc.Log.Information($"[TataruPraise] 移除超過 {maxLength} 字的句子 {removedCount} 句，刪掉 {deletedWavs} 個語音快取。");
        return removedCount;
    }

    /// <summary>把某句話的快取路徑寫回池裡。</summary>
    public void SetCachedWav(string text, string relativePath)
    {
        var changed = false;
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                foreach (var line in list)
                {
                    if (line.Text != text || line.Wav == relativePath) continue;
                    line.Wav = relativePath;
                    changed = true;
                }
            }
        }

        if (changed) Save();
    }

    /// <summary>某個情境目前有幾句。</summary>
    public int CountOf(string category)
    {
        lock (gate)
        {
            return pool.TryGetValue(category, out var list) ? list.Count : 0;
        }
    }

    /// <summary>
    /// 要列在 UI 上、也是「全部擴充」要跑的情境清單。
    /// </summary>
    /// <remarks>
    /// 內建情境在前（順序＝<see cref="PraiseCategory.All"/>），後面接 <c>pool.json</c> 裡出現過但
    /// 不在內建清單裡的自訂鍵。
    /// <para>
    /// 🔴 自訂鍵一定要列出來：<see cref="Load"/> 刻意保留不認得的鍵，如果 UI 只畫內建的四個，
    /// 使用者自己加的情境就會變成「檔案裡有、畫面上沒有」——那比沒有這個功能還糟。
    /// </para>
    /// <para>
    /// 📌 自訂鍵用 ordinal 排序，不用字典的列舉順序：<see cref="Dictionary{TKey,TValue}"/> 的列舉順序
    /// 沒有保證，拿它當 UI 順序會讓列偶爾跳動。
    /// </para>
    /// </remarks>
    public List<string> Categories()
    {
        var extras = new List<string>();
        lock (gate)
        {
            foreach (var key in pool.Keys)
            {
                if (Array.IndexOf(PraiseCategory.All, key) >= 0) continue;
                extras.Add(key);
            }
        }

        extras.Sort(StringComparer.Ordinal);

        var result = new List<string>(PraiseCategory.All.Length + extras.Count);
        result.AddRange(PraiseCategory.All);
        result.AddRange(extras);
        return result;
    }

    /// <summary>某個情境目前的所有句子（快照；情境不存在就回空清單）。</summary>
    public List<string> TextsOf(string category)
    {
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list)) return [];

            var texts = new List<string>(list.Count);
            foreach (var line in list) texts.Add(line.Text);
            return texts;
        }
    }

    /// <summary>
    /// 把整池重置回 <see cref="DefaultPool"/> 的內建句子。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>不可回復的破壞性操作</b>，只能由使用者在設定視窗做完兩段式確認之後觸發，
    /// 絕不可以在載入時、改設定時、或任何自動路徑上跑。
    /// <para>
    /// 🔴 <b>備份不成功就整個中止，一個位元組都不動。</b>刪的是使用者自己攢了很久的整池句子，
    /// 沒有備份就不准動手。備份＝把現有的 <c>pool.json</c> 原封不動複製成
    /// <c>pool.backup-yyyyMMdd-HHmmss.json</c> 放在同一個資料夾；<b>不清舊備份、不輪替</b>
    /// （那也是使用者的資料，要刪由他自己刪）。
    /// </para>
    /// <para>
    /// 🔴 刪 WAV 的範圍<b>只有「重置前池裡真的存在的句子」的雜湊檔名</b>——
    /// <b>不掃 <c>cache/</c> 目錄</b>。掃目錄刪檔會把使用者手動放進去的東西一起清掉，而且是靜默的。
    /// </para>
    /// <para>
    /// ⚠️ 內建那 28 句的 WAV <b>也會一起被刪掉</b>（它們本來就在舊池裡）：重置之後寫回去的是
    /// <b>沒有語音</b>的預設池，那 28 句要重新按一次「預合成」。這是刻意的——
    /// 讓「重置完的狀態」永遠是同一個，不會因為使用者以前合成過什麼而長得不一樣。
    /// </para>
    /// </remarks>
    /// <param name="backupPath">舊池的備份絕對路徑；原本就沒有 <c>pool.json</c> 時是空字串。</param>
    /// <param name="removedLines">重置前池裡有幾句（重置中止時是 0）。</param>
    /// <param name="deletedWavs">實際刪掉幾個 WAV 檔。</param>
    /// <param name="error">中止原因；成功是 <c>null</c>。</param>
    /// <returns>有沒有真的重置。</returns>
    public bool ResetToDefault(out string backupPath, out int removedLines, out int deletedWavs, out string? error)
    {
        backupPath = string.Empty;
        removedLines = 0;
        deletedWavs = 0;
        error = null;

        // ① 先抄下「重置前有哪些句子」——等一下就照這份清單刪 WAV，不掃目錄。
        var oldTexts = new HashSet<string>(StringComparer.Ordinal);
        var oldCount = 0;
        lock (gate)
        {
            foreach (var list in pool.Values)
            {
                foreach (var line in list)
                {
                    oldCount++;
                    oldTexts.Add(line.Text);
                }
            }
        }

        // ② 備份。失敗就直接放棄，不進第三步。
        try
        {
            Directory.CreateDirectory(dataDir);
            if (File.Exists(poolPath))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var candidate = Path.Combine(dataDir, $"pool.backup-{stamp}.json");

                // 同一秒內按第二次也不可以蓋掉上一份備份。
                for (var i = 2; i < 100 && File.Exists(candidate); i++)
                    candidate = Path.Combine(dataDir, $"pool.backup-{stamp}-{i}.json");

                File.Copy(poolPath, candidate, overwrite: false);
                backupPath = candidate;
            }
        }
        catch (Exception ex)
        {
            error = $"備份舊池失敗（{ex.Message}），什麼都沒有動。";
            Svc.Log.Information($"[TataruPraise] 重置誇獎池中止：{error}");
            return false;
        }

        // ③ 換成內建預設池（含自訂鍵在內，整個字典重建）。
        lock (gate)
        {
            pool = [];
            foreach (var (category, lines) in DefaultPool.Lines)
            {
                var target = new List<PraiseLine>(lines.Count);
                foreach (var text in lines) target.Add(new PraiseLine { Text = text });
                pool[category] = target;
            }

            foreach (var key in PraiseCategory.All) pool.TryAdd(key, []);
        }

        Save();
        removedLines = oldCount;

        // ④ 刪 WAV：只刪剛才抄下來那份清單裡的句子。
        foreach (var text in oldTexts)
        {
            var path = CachePathFor(text);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                deletedWavs++;
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 刪除語音快取失敗（{path}）：{ex.Message}");
            }
        }

        var where = backupPath.Length > 0 ? backupPath : "（原本就沒有 pool.json，沒有備份）";
        Svc.Log.Information(
            $"[TataruPraise] 重置誇獎池：清掉 {removedLines} 句、刪掉 {deletedWavs} 個語音快取，舊池備份於 {where}。");
        return true;
    }

    /// <summary>內建預設池一共有幾句（UI 的確認文字要寫得出這個數字）。</summary>
    public static int DefaultLineCount()
    {
        var n = 0;
        foreach (var lines in DefaultPool.Lines.Values) n += lines.Count;
        return n;
    }

    /// <summary>某個情境有幾句「語音快取檔真的在磁碟上」。</summary>
    public int CachedCountOf(string category)
    {
        List<string> texts;
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list)) return 0;
            texts = new List<string>(list.Count);
            foreach (var line in list) texts.Add(line.Text);
        }

        var n = 0;
        foreach (var text in texts)
        {
            if (File.Exists(CachePathFor(text))) n++;
        }

        return n;
    }

    /// <summary>整池所有句子的快照（擴充池／預合成用）。</summary>
    public List<(string Category, string Text)> Snapshot()
    {
        var result = new List<(string, string)>();
        lock (gate)
        {
            foreach (var (category, list) in pool)
            {
                foreach (var line in list) result.Add((category, line.Text));
            }
        }

        return result;
    }

    /// <summary>
    /// 從某個情境挑一句「語音快取真的存在」的話。挑不到回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 只挑有快取的：純池模式的整個賣點就是執行期零 HTTP，挑到沒合成的句子只會靜默不出聲，
    /// 使用者會以為外掛壞了。這裡直接把沒快取的濾掉，讓「有東西可播」與「沒東西可播」分得開。
    /// </remarks>
    public string? PickCached(string category)
    {
        List<string> candidates = [];
        lock (gate)
        {
            if (!pool.TryGetValue(category, out var list) || list.Count == 0) return null;
            foreach (var line in list) candidates.Add(line.Text);
        }

        // 過濾與挑選都在鎖外做：File.Exists 是磁碟 I/O，不要拿著鎖去等它。
        var playable = new List<string>(candidates.Count);
        foreach (var text in candidates)
        {
            if (File.Exists(CachePathFor(text))) playable.Add(text);
        }

        if (playable.Count == 0) return null;
        lock (gate)
        {
            return playable[random.Next(playable.Count)];
        }
    }

    /// <summary>
    /// 任何一個情境有可播的內容嗎（IPC 的 <c>IsAvailable</c> 用）。
    /// </summary>
    /// <remarks>
    /// 🔴 結果快取 2 秒。這個方法會對整池做 <see cref="File.Exists"/>，而它是<b>公開的 IPC 端點</b>——
    /// 呼叫端很可能在自己的每幀迴圈裡問它，沒有快取的話等於幫別人的外掛裝了一台磁碟壓力機。
    /// 2 秒的誤差對「現在能不能出聲」這個問題沒有意義（預合成本來就要跑好幾分鐘）。
    /// </remarks>
    public bool HasAnyCached()
    {
        lock (availabilityGate)
        {
            var now = DateTime.UtcNow;
            if (now - availabilityCheckedUtc < AvailabilityCacheDuration) return availabilityCache;
            availabilityCheckedUtc = now;
        }

        var any = false;
        foreach (var category in PraiseCategory.All)
        {
            if (PickCached(category) == null) continue;
            any = true;
            break;
        }

        lock (availabilityGate) availabilityCache = any;
        return any;
    }
}
