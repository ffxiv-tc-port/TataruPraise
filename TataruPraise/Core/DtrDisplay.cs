using System;
using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace TataruPraise.Core;

/// <summary>
/// 伺服器資訊列（DTR）上的一格：目前是開還是關，點一下直接切換總開關。
/// </summary>
/// <remarks>
/// 🔴 <b>DTR 的文字是遊戲的原生 <c>AtkTextNode</c> 畫的，不是 ImGui</b>
/// （見 Dalamud 的 <c>Game/Gui/Dtr/DtrBar.cs</c>：<c>node-&gt;SetText(...)</c>）。
/// 所以<b>塞 FontAwesome 的字元進來是不會顯示的</b>——那套字型只存在於 Dalamud 的 ImGui 字型圖集裡。
/// 這裡改用遊戲自己的圖示字元（<see cref="SeIconChar"/>），那是原生節點畫得出來的東西。
/// <para>
/// 🔴 點一下＝<b>直接切換總開關並立刻存檔</b>，不是「暫停」。使用者要的是一個開關，
/// 而「暫停」會產生一個設定檔看不到的第三種狀態——關掉遊戲再開就悄悄變回去了。
/// </para>
/// <para>
/// 📌 「開／關」這種要隨時掃視的資訊放在格子上（圖示 + 「塔塔露」）；
/// 「最近念了什麼」「有幾個情境沒語音」這種起疑才查的放 tooltip。
/// </para>
/// </remarks>
public sealed class DtrDisplay : IDisposable
{
    /// <summary>DTR 格的標題（Dalamud 設定裡使用者看到的名字，也是 <see cref="IDtrBar.Get"/> 的鍵）。</summary>
    private const string EntryTitle = "TataruPraise";

    /// <summary>格子上的短文字。</summary>
    private const string ShortLabel = "塔塔露";

    /// <summary>
    /// tooltip 的重算間隔。
    /// </summary>
    /// <remarks>
    /// 🔴 「有幾個情境沒語音」要對整池做 <see cref="System.IO.File.Exists"/>。
    /// 每幀算等於在 framework tick 上開一台磁碟壓力機，而 tooltip 晚五秒更新沒有人看得出來。
    /// </remarks>
    private static readonly TimeSpan TooltipInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration config;
    private readonly PraisePool pool;
    private readonly PraiseService service;
    private readonly IDtrBarEntry entry;

    /// <summary>上一次寫進格子的開關狀態；<c>null</c>＝還沒寫過。</summary>
    private bool? lastEnabled;

    private DateTime lastTooltipUtc = DateTime.MinValue;
    private bool disposed;

    public DtrDisplay(Configuration config, PraisePool pool, PraiseService service)
    {
        this.config = config;
        this.pool = pool;
        this.service = service;

        entry = Svc.DtrBar.Get(EntryTitle);
        entry.OnClick = OnClick;

        RefreshText(force: true);
        RefreshTooltip();

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Svc.Framework.Update -= OnFrameworkUpdate;

        // 🔴 一定要 Remove：不移除的話這一格會留在資訊列上直到重開遊戲，
        //    而且它掛著的 OnClick 指向已經被卸載的外掛。
        try
        {
            entry.Remove();
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 移除 DTR 格失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 點一下＝切換總開關。
    /// </summary>
    /// <remarks>
    /// 🔴 立刻 <see cref="Configuration.Save"/>：使用者按完就可能直接關遊戲，
    /// 不存的話那一下會在關閉的瞬間靜默消失。
    /// </remarks>
    private void OnClick(DtrInteractionEvent _)
    {
        config.Enabled = !config.Enabled;
        config.Save();

        Svc.Log.Information($"[TataruPraise] 從資訊列切換總開關：{(config.Enabled ? "開" : "關")}。");
        RefreshText(force: true);
        RefreshTooltip();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        RefreshText(force: false);

        var now = DateTime.UtcNow;
        if (now - lastTooltipUtc < TooltipInterval) return;
        lastTooltipUtc = now;
        RefreshTooltip();
    }

    /// <summary>格子上的圖示與文字；狀態沒變就什麼都不做（DTR 的每次寫入都會重建原生節點的字串）。</summary>
    private void RefreshText(bool force)
    {
        var enabled = config.Enabled;
        if (!force && lastEnabled == enabled) return;
        lastEnabled = enabled;

        // 📌 遊戲原生的圖示字元：開＝○（遊戲自己就用它表示「是」），關＝🚫。
        //    FontAwesome 的 VolumeUp／VolumeMute 在原生文字節點上畫不出來，見類別註解。
        var icon = enabled ? SeIconChar.Circle : SeIconChar.Prohibited;
        entry.Text = new SeString(new TextPayload($"{(char)icon}{ShortLabel}"));
    }

    private void RefreshTooltip()
    {
        var sb = new StringBuilder();
        sb.Append(config.Enabled ? "總開關：開" : "總開關：關");
        sb.Append("（點一下切換）");

        var recent = service.RecentHistory();
        sb.Append('\n').Append("最近 ").Append(recent.Count).Append(" 次觸發：");
        if (recent.Count == 0)
        {
            // 🔴 「還沒有」要寫出來。留白會讓人以為是 tooltip 壞了。
            sb.Append('\n').Append("　（這次啟動後還沒出過聲）");
        }
        else
        {
            foreach (var (when, category, text) in recent)
                sb.Append('\n').Append("　").Append(when.ToString("HH:mm:ss")).Append('　')
                  .Append(category).Append('　').Append(text);
        }

        sb.Append('\n').Append("沒有語音的情境數：").Append(SilentCategoryCount());
        entry.Tooltip = new SeString(new TextPayload(sb.ToString()));
    }

    /// <summary>有幾個情境「一句可播的都沒有」——那些情境現在觸發也是靜默的。</summary>
    private int SilentCategoryCount()
    {
        var n = 0;
        foreach (var category in pool.Categories())
        {
            if (pool.CachedCountOf(category) == 0) n++;
        }

        return n;
    }
}
