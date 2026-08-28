using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TataruPraise.Core;

/// <summary>
/// 出聲的總機：總開關、冷卻、機率、挑句、播放。
/// </summary>
/// <remarks>
/// 三個入口的規則刻意不一樣，README 與 IPC 契約都照這裡寫：
/// <list type="table">
/// <item><term><see cref="TryTrigger"/>（遊戲事件）</term><description>總開關 → 該事件開關 → 冷卻 → 機率</description></item>
/// <item><term><see cref="Praise"/>（IPC）</term><description>總開關 → 冷卻（<b>不看</b>事件開關與機率）</description></item>
/// <item><term><see cref="Speak"/>（IPC）</term><description>總開關（<b>不看</b>冷卻與機率——呼叫端是明確要求念這一句）</description></item>
/// </list>
/// 三者都受「同時只播一句」限制：正在播的時候後來的一律丟棄並回 <c>false</c>。
/// </remarks>
public sealed class PraiseService : IDisposable
{
    private readonly Configuration config;
    private readonly PraisePool pool;
    private readonly AudioPlayer audio = new();
    private readonly object cooldownGate = new();

    private DateTime lastSpokenUtc = DateTime.MinValue;

    public PraiseService(Configuration config, PraisePool pool)
    {
        this.config = config;
        this.pool = pool;
    }

    public AudioPlayer Audio => audio;

    /// <summary>距離下次可以出聲還有幾秒（0＝現在就可以）。UI 上要看得見，所以是公開的。</summary>
    public double CooldownRemainingSeconds
    {
        get
        {
            lock (cooldownGate)
            {
                var elapsed = (DateTime.UtcNow - lastSpokenUtc).TotalSeconds;
                var remain = config.CooldownSeconds - elapsed;
                return remain > 0 ? remain : 0;
            }
        }
    }

    /// <summary>
    /// 每個情境上一次出聲的時間（UTC ticks）。
    /// </summary>
    /// <remarks>
    /// 🔴 冷卻計時器是<b>逐情境</b>的。共用一個計時器的話，AutoRetainer 多角色連跑時
    /// 後面幾個角色的「潛艇」通知會被前一個吃掉；戰鬥警示更是完全等不起兩分鐘。
    /// <para>
    /// 📌 「同時只播一句」的限制<b>沒有</b>跟著拆開——那是喇叭的物理限制，不是節流政策。
    /// 正在播的時候來的東西一律丟棄（不排隊），並在 Debug 記一行。
    /// </para>
    /// <para>
    /// 🔴 這個字典會被<b>呼叫端的執行緒</b>碰到（IPC 在對方執行緒上跑），所以跟 cooldownGate 共用同一把鎖。
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, long> lastSpokenPerCategory = new(StringComparer.Ordinal);

    /// <summary>某個情境距離下次可以出聲還有幾秒（0＝現在就可以）。</summary>
    public double CooldownRemainingSecondsOf(string category)
    {
        var cooldown = config.CooldownOf(category);
        if (cooldown <= 0) return 0;

        lock (cooldownGate)
        {
            if (!lastSpokenPerCategory.TryGetValue(category, out var ticks)) return 0;

            var elapsed = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
            var remain = cooldown - elapsed;
            return remain > 0 ? remain : 0;
        }
    }

    /// <summary>總開關開著、而且真的有可播的內容。</summary>
    public bool IsAvailable() => config.Enabled && pool.HasAnyCached();

    /// <summary>
    /// 遊戲事件觸發的路徑：吃冷卻也吃機率。
    /// </summary>
    /// <param name="category">情境。</param>
    /// <param name="chanceOverride">
    /// 這一次要用的機率（%）。<c>null</c>＝用 <see cref="Configuration.ChancePercent"/>。
    /// </param>
    /// <remarks>
    /// 📌 <paramref name="chanceOverride"/> 是給「首次通關」用的：同一個事件、同一個情境，
    /// 但這一次的機率不一樣。<b>不要</b>為此另開一個情境——情境是 pool.json 的鍵，
    /// 多開一個等於使用者要多養一池句子。
    /// </remarks>
    public bool TryTrigger(string category, int? chanceOverride = null)
    {
        if (!config.Enabled) return false;
        if (CooldownRemainingSecondsOf(category) > 0) return false;

        var chance = Math.Clamp(chanceOverride ?? config.ChancePercent, 0, 100);
        if (chance <= 0) return false;
        if (chance < 100 && Random.Shared.Next(100) >= chance) return false;

        return PlayFromPool(category);
    }

    /// <summary>這個情境存在嗎（IPC 要分得出「未知情境」與「有情境但沒句子」）。</summary>
    public bool HasCategory(string category) => pool.HasCategory(category);

    /// <summary>IPC <c>TataruPraise.Praise</c>：無視事件開關與機率，但吃冷卻。</summary>
    public bool Praise(string category)
    {
        if (!config.Enabled) return false;
        if (CooldownRemainingSecondsOf(category) > 0) return false;
        return PlayFromPool(category);
    }

    /// <summary>
    /// IPC <c>TataruPraise.Speak</c>：念指定的句子。
    /// </summary>
    /// <remarks>
    /// 先查語音快取；沒有的話丟一個背景 Task 去 9882 即時合成（逾時 10 秒），合成好順便寫進快取。
    /// 回傳值是「有沒有排進去」——即時合成那條路是<b>非同步</b>的，回 <c>true</c> 只代表已受理，
    /// 不代表真的出得了聲（橋接連不上就只是不出聲）。
    /// </remarks>
    public bool Speak(string text)
    {
        if (!config.Enabled) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (audio.IsBusy) return false;

        var trimmed = text.Trim();
        var cached = pool.CachePathFor(trimmed);
        if (File.Exists(cached))
        {
            var queued = audio.TryPlayFile(cached, config.Volume);
            if (queued) MarkSpoken();
            return queued;
        }

        // 即時合成：不在呼叫端的執行緒上等 HTTP。
        var host = config.TtsHost;
        var voice = config.VoiceId;
        var volume = config.Volume;
        _ = Task.Run(async () =>
        {
            var wav = await TtsBridge.SynthesizeAsync(host, voice, trimmed, 10).ConfigureAwait(false);
            if (wav == null) return;

            TryWriteCache(trimmed, wav);
            audio.TryPlay(wav, volume);
        });

        MarkSpoken();
        return true;
    }

    /// <summary>試播：直接從池裡挑一句（總開關關著也可以，因為這是使用者按的按鈕）。</summary>
    public bool PlayTest(out string message)
    {
        foreach (var category in PraiseCategory.All)
        {
            var text = pool.PickCached(category);
            if (text == null) continue;

            var path = pool.CachePathFor(text);
            if (!audio.TryPlayFile(path, config.Volume))
            {
                message = "上一句還在播，等它播完再試。";
                return false;
            }

            message = text;
            return true;
        }

        message = "池裡沒有任何「已經合成好語音」的句子，先按「預合成語音快取」。";
        return false;
    }

    private bool PlayFromPool(string category)
    {
        var text = pool.PickCached(category);
        if (text == null)
        {
            Svc.Log.Information(
                $"[TataruPraise] 情境「{category}」沒有已合成語音的句子，這次不出聲"
                + $"（池裡 {pool.CountOf(category)} 句，已快取 {pool.CachedCountOf(category)} 句）。");
            return false;
        }

        var queued = audio.TryPlayFile(pool.CachePathFor(text), config.Volume);
        if (!queued)
        {
            // 上一句還在播。通知／警示不排隊——等它播完再喊「後面！」已經沒有意義了。
            Svc.Log.Debug($"[TataruPraise] 情境「{category}」：上一句還在播，這次丟棄。");
            return false;
        }

        MarkSpoken(category);
        Svc.Log.Information($"[TataruPraise] 觸發「{category}」：{text}");
        return true;
    }

    /// <summary>記下「剛剛出聲了」：全域那份給 UI 顯示用，逐情境那份才是真的冷卻閘門。</summary>
    private void MarkSpoken(string category = "")
    {
        var now = DateTime.UtcNow;
        lock (cooldownGate)
        {
            lastSpokenUtc = now;
            if (category.Length > 0) lastSpokenPerCategory[category] = now.Ticks;
        }
    }

    private void TryWriteCache(string text, byte[] wav)
    {
        try
        {
            Directory.CreateDirectory(pool.CacheDirectory);
            var path = pool.CachePathFor(text);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, wav);
            File.Move(tmp, path, overwrite: true);
            pool.SetCachedWav(text, "cache/" + PraisePool.CacheFileName(text));
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 寫入語音快取失敗：{ex.Message}");
        }
    }

    public void Dispose() => audio.Dispose();
}
