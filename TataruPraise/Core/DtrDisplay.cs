using System;
using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
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
/// ⚠️ 而且<b>遊戲字型裡沒有的碼位不會報錯，只會靜默畫成 <c>〓</c></b>
/// （<c>FdtReader.GetGlyph</c> 的 fallback 鏈＝U+3013 →「?」→「=」）。
/// ⇒ 任何非 ASCII 字元寫進這一格之前，都要先離線證明台服字型真的有那個 glyph——
/// 查法見 <see cref="GlyphOn"/> 的註解。
/// <para>
/// 📌 用<b>音符</b>表示「會出聲」而不是鈴鐺：鈴鐺（<see cref="BitmapFontIcon.Alarm"/>）
/// 已經被 AutoRetainer 的 <c>MultiModeDtr</c> 佔用了，資訊列上並排兩個鈴鐺分不出誰是誰。
/// </para>
/// <para>
/// 🔴 <b>左鍵</b>＝直接切換音訊總開關並立刻存檔，不是「暫停」。使用者要的是一個開關，
/// 而「暫停」會產生一個設定檔看不到的第三種狀態——關掉遊戲再開就悄悄變回去了。
/// <b>右鍵</b>＝開啟設定視窗（艦隊慣例：AutoRetainer、TCToolbox、YesAlready 的 DTR 格都是右鍵開設定）。
/// </para>
/// <para>
/// 📌 「開／靜音」這種要隨時掃視的資訊放在格子上，而且<b>只放圖示、不放文字</b>——
/// 圖示本身已經說完狀態，多一個「塔塔露」只是占資訊列的寬度。
/// 純符號的先例＝Accountant 的 <c>DtrManager</c>
/// （<c>SeIconChar.BoxedLetterC.ToIconString()</c> 直接寫進 <c>entry.Text</c>，出貨中）。
/// 「最近念了什麼」「有幾個情境沒語音」這種起疑才查的放 tooltip。
/// </para>
/// </remarks>
public sealed class DtrDisplay : IDisposable
{
    /// <summary>DTR 格的標題（Dalamud 設定裡使用者看到的名字，也是 <see cref="IDtrBar.Get"/> 的鍵）。</summary>
    private const string EntryTitle = "TataruPraise";

    /// <summary>音訊開著時格子上的字元：四分音符「♪」（U+266A）。</summary>
    /// <remarks>
    /// 🔴 <b>這個字在台服遊戲字型裡真的存在，不是猜的。</b>
    /// 離線查證（2026-08-29，工具 <c>~/.claude/tools/sqpack/fontglyph/fontglyph.py</c>，
    /// 直讀台服 sqpack 的 <c>common/font/AXIS_*.fdt</c> 字型表並帶雙向校準閘門）：
    /// U+266A 在 <c>AXIS_12／14／18／36</c> 四個字型<b>全部有 glyph</b>（12pt 時 14×14px），
    /// 同一份表裡也查得到「音」等中文字，證明台服的原生文字節點就是吃這幾個 AXIS 字型。
    /// <para>
    /// ⚠️ <b>同一次查證也證明 U+266B「♫」與 U+2669「♩」在台服字型裡不存在</b>——
    /// 想換成別的音符會直接變成 <c>〓</c>。要換符號之前先拿那支工具查過再改。
    /// </para>
    /// </remarks>
    private const string GlyphOn = "♪";

    /// <summary>音訊關掉時格子上的字元：禁止符號（<see cref="SeIconChar.Prohibited"/>，U+E043）。</summary>
    /// <remarks>
    /// 📌 兩個狀態都走<b>純文字</b>（<see cref="TextPayload"/>）而不是 <see cref="IconPayload"/>：
    /// 一邊圖示區塊、一邊文字會讓格子寬度在切換時跳動，同一條路徑比較穩。
    /// <c>SeIconChar</c> 的 PUA 字元一樣由原生文字繪製器展開——
    /// 艦隊先例＝Accountant 的 <c>DtrManager</c>。離線查證同上：U+E043 四個 AXIS 字型全部有 glyph。
    /// </remarks>
    private static readonly string GlyphOff = SeIconChar.Prohibited.ToIconString();

    /// <summary>
    /// 🔴 <b>退路</b>：萬一實機上「♪」還是畫成豆腐，把 <see cref="RefreshText"/> 裡那一行改回
    /// <c>new SeString(new IconPayload(enabled ? FallbackIconOn : FallbackIconOff))</c> 就回到 v7.20.0.9 的行為。
    /// </summary>
    /// <remarks>
    /// 📌 遊戲的點陣圖示表<b>沒有喇叭</b>（<c>SeIconChar</c> 與 <see cref="BitmapFontIcon"/> 全表都查過），
    /// 所以退路只能用語意最接近的鬧鈴／勿擾這一組。
    /// </remarks>
    private const BitmapFontIcon FallbackIconOn = BitmapFontIcon.Alarm;

    /// <inheritdoc cref="FallbackIconOn"/>
    private const BitmapFontIcon FallbackIconOff = BitmapFontIcon.DoNotDisturb;

    /// <summary>音訊開著時要寫進格子的內容。</summary>
    private static SeString IconOn() => new(new TextPayload(GlyphOn));

    /// <summary>音訊靜音時要寫進格子的內容。</summary>
    private static SeString IconOff() => new(new TextPayload(GlyphOff));

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

        // 📌 開＝音符「♪」（會出聲），關＝禁止符號（靜音）。純符號、不加文字。
        //    兩個字元都已離線證明存在於台服的 AXIS 字型，見 GlyphOn／GlyphOff 的註解。
        entry.Text = enabled ? IconOn() : IconOff();
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
