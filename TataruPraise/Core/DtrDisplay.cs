using System;
using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace TataruPraise.Core;

/// <summary>
/// 伺服器資訊列（DTR）上的一格：用一個圖示表示語音現在會不會出聲，左鍵切換、右鍵開設定。
/// </summary>
/// <remarks>
/// 🔴 <b>DTR 的文字是遊戲的原生 <c>AtkTextNode</c> 畫的，不是 ImGui</b>
/// （見 Dalamud 的 <c>Game/Gui/Dtr/DtrBar.cs</c>：<c>node-&gt;SetText(...)</c>）。
/// 所以<b>塞 FontAwesome 的字元進來是不會顯示的</b>——那套字型只存在於 Dalamud 的 ImGui 字型圖集裡。
/// 這裡改用<b>遊戲自己的點陣圖示</b>（<see cref="BitmapFontIcon"/> ＋ <see cref="IconPayload"/>）：
/// 它編碼成 SeString 的原生圖示區塊（<c>SeStringChunkType.Icon</c>），由遊戲的文字繪製器自己展開，
/// 和 ImGui 字型完全無關，也不是 <c>SeIconChar</c> 那種 PUA 字元。
/// 艦隊先例＝AutoRetainer 的 <c>MultiModeDtr</c>（同樣用 <c>BitmapFontIcon.Alarm</c>）。
/// <para>
/// 🔴 <b>左鍵</b>＝直接切換音訊總開關並立刻存檔，不是「暫停」。使用者要的是一個開關，
/// 而「暫停」會產生一個設定檔看不到的第三種狀態——關掉遊戲再開就悄悄變回去了。
/// <b>右鍵</b>＝開啟設定視窗（艦隊慣例：AutoRetainer、TCToolbox、YesAlready 的 DTR 格都是右鍵開設定）。
/// </para>
/// <para>
/// 📌 「開／靜音」這種要隨時掃視的資訊放在格子上，而且<b>只放圖示、不放文字</b>——
/// 圖示本身已經說完狀態，多一個「塔塔露」只是占資訊列的寬度。
/// 純圖示的先例＝EurekaHelper 的 DTR 格（<c>new SeString(new IconPayload(...))</c>，完全沒有 TextPayload）。
/// 「最近念了什麼」「有幾個情境沒語音」這種起疑才查的放 tooltip。
/// </para>
/// </remarks>
public sealed class DtrDisplay : IDisposable
{
    /// <summary>DTR 格的標題（Dalamud 設定裡使用者看到的名字，也是 <see cref="IDtrBar.Get"/> 的鍵）。</summary>
    private const string EntryTitle = "TataruPraise";

    /// <summary>音訊開著時的圖示：鬧鈴（會出聲）。</summary>
    /// <remarks>
    /// 📌 遊戲的點陣圖示表<b>沒有喇叭</b>（<c>SeIconChar</c> 與 <see cref="BitmapFontIcon"/> 全表都查過），
    /// 所以用語意最接近「會出聲／被靜音」的這一組。AutoRetainer 的 DTR 格也是拿 Alarm 當鈴鐺用。
    /// </remarks>
    private const BitmapFontIcon IconOn = BitmapFontIcon.Alarm;

    /// <summary>音訊關掉時的圖示：勿擾（靜音）。</summary>
    private const BitmapFontIcon IconOff = BitmapFontIcon.DoNotDisturb;

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

    /// <summary>右鍵要做的事：開關設定視窗。</summary>
    private readonly Action openConfig;

    /// <summary>上一次寫進格子的開關狀態；<c>null</c>＝還沒寫過。</summary>
    private bool? lastEnabled;

    private DateTime lastTooltipUtc = DateTime.MinValue;
    private bool disposed;

    public DtrDisplay(Configuration config, PraisePool pool, PraiseService service, Action openConfig)
    {
        this.config = config;
        this.pool = pool;
        this.service = service;
        this.openConfig = openConfig;

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
    /// 左鍵＝切換音訊總開關；右鍵＝開啟設定視窗。
    /// </summary>
    /// <remarks>
    /// 🔴 立刻 <see cref="Configuration.Save"/>：使用者按完就可能直接關遊戲，
    /// 不存的話那一下會在關閉的瞬間靜默消失。
    /// <para>
    /// 📌 只有 <see cref="MouseClickType.Right"/> 走設定視窗，其餘一律當成左鍵。
    /// Dalamud 的 <c>DtrInteractionEvent.FromMouseEvent</c> 是
    /// <c>IsLeftClick ? Left : Right</c>，所以中鍵之類的會被歸成 Right——
    /// 讓「非左鍵」去開一個視窗，比讓它靜默翻轉總開關安全。
    /// </para>
    /// </remarks>
    private void OnClick(DtrInteractionEvent ev)
    {
        if (ev.ClickType == MouseClickType.Right)
        {
            openConfig();
            return;
        }

        config.Enabled = !config.Enabled;
        config.Save();

        Svc.Log.Information($"[TataruPraise] 從資訊列切換音訊：{(config.Enabled ? "開" : "靜音")}。");
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

    /// <summary>格子上的圖示；狀態沒變就什麼都不做（DTR 的每次寫入都會重建原生節點的字串）。</summary>
    private void RefreshText(bool force)
    {
        var enabled = config.Enabled;
        if (!force && lastEnabled == enabled) return;
        lastEnabled = enabled;

        // 📌 開＝鬧鈴（會出聲），關＝勿擾（靜音）。純圖示、不加文字。
        //    走 IconPayload 是因為那是原生文字節點畫得出來的點陣圖示，見類別註解。
        var icon = enabled ? IconOn : IconOff;
        entry.Text = new SeString(new IconPayload(icon));
    }

    private void RefreshTooltip()
    {
        var sb = new StringBuilder();
        sb.Append(config.Enabled ? "音訊：開" : "音訊：靜音");
        sb.Append('\n').Append("左鍵：切換音訊　右鍵：開啟設定");

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
