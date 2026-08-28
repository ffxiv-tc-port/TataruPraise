using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TataruPraise.Core;

/// <summary>
/// 設定 UI 那兩個按鈕背後的長時間工作：擴充誇獎池（Gemini）、預合成語音快取（9882）。
/// </summary>
/// <remarks>
/// 兩個工作都在背景 Task 上跑，同時只准跑一個。進度與最後結果放在欄位裡讓 UI 每幀讀
/// （<c>volatile</c>／簡單型別，沒有鎖——UI 讀到中間狀態最多就是慢一幀）。
/// <para>
/// 📌 <b>「上一次做了什麼」要留在列上看得見</b>，不能只寫進 log：使用者按了按鈕之後，
/// 唯一能判斷「是成功了還是根本沒動」的地方就是那一行。
/// </para>
/// </remarks>
public sealed class PoolJobs : IDisposable
{
    private readonly Configuration config;
    private readonly PraisePool pool;

    private CancellationTokenSource? cts;
    private volatile bool running;
    private volatile string jobName = string.Empty;
    private volatile string progress = string.Empty;
    private volatile string lastResult = string.Empty;

    public PoolJobs(Configuration config, PraisePool pool)
    {
        this.config = config;
        this.pool = pool;
    }

    public bool IsRunning => running;

    /// <summary>正在跑的工作名稱（沒在跑就是空字串）。</summary>
    public string JobName => jobName;

    /// <summary>進度短句，例如「3/28」。</summary>
    public string Progress => progress;

    /// <summary>上一次的結果（可能很長，UI 上截短、完整放 tooltip）。</summary>
    public string LastResult => lastResult;

    public void Cancel() => cts?.Cancel();

    /// <summary>擴充誇獎池：對每個情境各要 <see cref="Configuration.GenerateCountPerCategory"/> 句。</summary>
    public bool StartExpandPool()
    {
        if (running) return false;
        if (string.IsNullOrWhiteSpace(config.GeminiApiKey))
        {
            lastResult = "沒有填 Gemini API 金鑰，沒有東西可以擴充。";
            return false;
        }

        return Start("擴充誇獎池", ExpandPoolAsync);
    }

    /// <summary>預合成語音快取：把池裡還沒有 WAV 的句子逐句送去 9882。</summary>
    public bool StartPrecacheAudio()
    {
        if (running) return false;
        return Start("預合成語音快取", PrecacheAudioAsync);
    }

    /// <summary>
    /// 移除池裡超過句長上限的句子（連同語音快取）。
    /// </summary>
    /// <remarks>
    /// 🔴 只能由使用者在設定視窗明確按下去才會跑：刪的是使用者自己的 pool.json 內容，不可回復。
    /// 改滑桿、載入外掛、擴充池都<b>不會</b>順手清舊句子。
    /// </remarks>
    public bool StartPruneLongLines()
    {
        if (running) return false;

        var max = ClampedMaxLength();
        return Start("移除超長句子", _ =>
        {
            var removed = pool.RemoveLongerThan(max, out var wavs);
            return Task.FromResult(
                removed == 0
                    ? $"移除超長句子：池裡沒有超過 {max} 字的句子，什麼都沒動。"
                    : $"移除超長句子：刪掉 {removed} 句、連帶刪掉 {wavs} 個語音快取（上限 {max} 字）。");
        });
    }

    /// <summary>句長上限（夾在 UI 滑桿的範圍內；設定檔被手改成離譜的值也不會炸）。</summary>
    private int ClampedMaxLength()
        => Math.Clamp(config.MaxPraiseLength, PraiseText.SliderMin, PraiseText.SliderMax);

    private bool Start(string name, Func<CancellationToken, Task<string>> work)
    {
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        running = true;
        jobName = name;
        progress = string.Empty;
        lastResult = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                lastResult = await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lastResult = $"{name}：已取消。";
            }
            catch (Exception ex)
            {
                lastResult = $"{name}：失敗（{ex.Message}）。";
                Svc.Log.Information($"[TataruPraise] {name} 失敗：{ex}");
            }
            finally
            {
                running = false;
                jobName = string.Empty;
                progress = string.Empty;
                Svc.Log.Information($"[TataruPraise] {name} 結束：{lastResult}");
            }
        }, token);

        return true;
    }

    private async Task<string> ExpandPoolAsync(CancellationToken token)
    {
        var key = config.GeminiApiKey;
        var model = config.GeminiModel;
        var count = Math.Clamp(config.GenerateCountPerCategory, 1, 50);
        var maxLength = ClampedMaxLength();

        var total = 0;
        var details = new List<string>();
        var stats = new GenerateStats();

        for (var i = 0; i < PraiseCategory.All.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var category = PraiseCategory.All[i];
            progress = $"{i + 1}/{PraiseCategory.All.Length}　{category}";

            var lines = await GeminiClient
                .GenerateAsync(key, model, category, count, maxLength, token, stats)
                .ConfigureAwait(false);
            var added = pool.AddLines(category, lines, out var duplicates);
            stats.Duplicate += duplicates;
            total += added;
            details.Add($"{category} +{added}");
        }

        // 🔴 被過濾掉的數字要跟著結果一起回去：全部被丟掉的時候，「一句都沒加」與
        // 「生了 40 句但全都太長」是完全不同的兩件事，使用者要能分得出來。
        var dropped = stats.Describe();
        var droppedSuffix = dropped.Length > 0 ? $"（{dropped}，上限 {maxLength} 字）" : string.Empty;

        if (total == 0)
        {
            return stats.AnyDropped
                ? $"擴充誇獎池：一句都沒有加進去{droppedSuffix}。上限太緊的話可以把「句長上限」調大一點。"
                : "擴充誇獎池：一句都沒有加進去（金鑰、模型名或額度的問題，詳見記錄檔）。";
        }

        return $"擴充誇獎池：新增 {total} 句（{string.Join("、", details)}）{droppedSuffix}。"
             + "新句子還沒有語音，記得接著按「預合成語音快取」。";
    }

    private async Task<string> PrecacheAudioAsync(CancellationToken token)
    {
        var host = config.TtsHost;
        var voice = config.VoiceId;

        var all = pool.Snapshot();
        var pending = new List<(string Category, string Text)>();
        foreach (var item in all)
        {
            if (!File.Exists(pool.CachePathFor(item.Text))) pending.Add(item);
        }

        if (pending.Count == 0)
            return $"預合成語音快取：池裡 {all.Count} 句全部都已經有語音了，沒有要做的事。";

        try
        {
            Directory.CreateDirectory(pool.CacheDirectory);
        }
        catch (Exception ex)
        {
            return $"預合成語音快取：建立快取資料夾失敗（{ex.Message}）。";
        }

        var ok = 0;
        var failed = 0;

        for (var i = 0; i < pending.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            progress = $"{i + 1}/{pending.Count}";

            var text = pending[i].Text;

            // 🔴 預合成用 60 秒逾時：一句可能要跑好幾秒，而這是使用者主動按的批次工作，不是遊戲中的即時路徑。
            var wav = await TtsBridge.SynthesizeAsync(host, voice, text, 60).ConfigureAwait(false);
            if (wav == null)
            {
                failed++;
                continue;
            }

            try
            {
                var path = pool.CachePathFor(text);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, wav, token).ConfigureAwait(false);
                File.Move(tmp, path, overwrite: true);
                pool.SetCachedWav(text, "cache/" + PraisePool.CacheFileName(text));
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                Svc.Log.Information($"[TataruPraise] 寫入語音快取失敗：{ex.Message}");
            }
        }

        var result = $"預合成語音快取：成功 {ok} 句";
        if (failed > 0)
            result += $"，失敗 {failed} 句（橋接連不上或聲線沒設定，詳見記錄檔）";
        return result + "。";
    }

    public void Dispose()
    {
        try { cts?.Cancel(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 取消背景工作失敗：{ex.Message}"); }
        cts?.Dispose();
        cts = null;
    }
}
