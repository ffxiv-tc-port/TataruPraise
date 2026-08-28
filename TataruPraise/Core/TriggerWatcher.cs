using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TataruPraise.Core;

/// <summary>
/// 遊戲事件 → 誇獎的四條觸發線。
/// </summary>
/// <remarks>
/// 🔴 <b>刻意不做任何靠聊天文字比對的觸發</b>（成就、製作大成功…）。台服的中文訊息字面沒辦法離線確定，
/// 憑印象或照國際服寫死一定錯，而且錯法是靜默的（比不到就永遠不觸發，看起來像功能沒做）。
/// 這四條全部是 Dalamud 的結構化事件或直接讀遊戲數值，沒有字串比對。
/// </remarks>
public sealed class TriggerWatcher : IDisposable
{
    /// <summary>Gil 的輪詢間隔。5 秒對「跨過一百萬」這種粒度綽綽有餘，而且完全不佔 tick。</summary>
    private static readonly TimeSpan GilPollInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration config;
    private readonly PraiseService service;

    /// <summary>各職業上一次看到的等級。<b>沒有紀錄就不觸發</b>，見 <see cref="OnLevelChanged"/>。</summary>
    private readonly Dictionary<uint, uint> knownLevels = [];

    private DateTime lastGilPollUtc = DateTime.MinValue;

    /// <summary>上一次讀到的 Gil。<c>-1</c>＝還沒讀到過（登入後第一次讀不算里程碑）。</summary>
    private long lastGil = -1;

    /// <summary>Gil 讀取失敗過就不再重試，也只寫一次 log（避免每 5 秒洗一行）。</summary>
    private bool gilReadBroken;

    private bool disposed;

    public TriggerWatcher(Configuration config, PraiseService service)
    {
        this.config = config;
        this.service = service;

        Svc.DutyState.DutyCompleted += OnDutyCompleted;
        Svc.ClientState.LevelChanged += OnLevelChanged;
        Svc.ClientState.Login += OnLogin;
        Svc.ClientState.Logout += OnLogout;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Svc.DutyState.DutyCompleted -= OnDutyCompleted;
        Svc.ClientState.LevelChanged -= OnLevelChanged;
        Svc.ClientState.Login -= OnLogin;
        Svc.ClientState.Logout -= OnLogout;
        Svc.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnDutyCompleted(object? sender, ushort territoryType)
    {
        if (!config.TriggerDutyComplete) return;
        service.TryTrigger(PraiseCategory.DutyComplete);
    }

    /// <summary>
    /// 升等。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>只有「這個職業之前就有紀錄，而且新等級比舊的高」才算升等。</b>
    /// 理由有兩個，兩個都會讓使用者在完全沒升等的時候聽到誇獎：
    /// <list type="number">
    /// <item>登入時客戶端會把目前職業的等級「回報」一次，那不是升等。</item>
    /// <item>切職業（<c>ClassJobChanged</c> 之後）會回報新職業的等級，那也不是升等，
    /// 而且從 90 級切到 1 級的職業時新值還比舊值小。</item>
    /// </list>
    /// 第一次看到某個職業就只記下來、不出聲——代價是「登入後第一次升等」也不會漏，因為那時候已經有紀錄了。
    /// <para>⚠️ 這兩件事都是<b>離線推理</b>，實機上 <c>LevelChanged</c> 到底在登入時會不會發、發幾次，
    /// 我沒有辦法在這裡證明。上面的寫法讓「會發」與「不會發」兩種情況都不會誤觸。</para>
    /// </remarks>
    private void OnLevelChanged(uint classJobId, uint level)
    {
        var hadPrevious = knownLevels.TryGetValue(classJobId, out var previous);
        knownLevels[classJobId] = level;

        if (!config.TriggerLevelUp) return;
        if (!hadPrevious || level <= previous) return;

        service.TryTrigger(PraiseCategory.LevelUp);
    }

    private void OnLogin()
    {
        // 🔴 每次登入都要清乾淨：換角色時舊角色的等級表留著，會讓新角色的第一次等級回報被當成升等。
        knownLevels.Clear();
        lastGil = -1;

        if (!config.TriggerLogin) return;
        service.TryTrigger(PraiseCategory.Login);
    }

    private void OnLogout(int type, int code)
    {
        knownLevels.Clear();
        lastGil = -1;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!config.TriggerGilMilestone) return;
        if (gilReadBroken) return;
        if (!Svc.ClientState.IsLoggedIn)
        {
            lastGil = -1;
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastGilPollUtc < GilPollInterval) return;
        lastGilPollUtc = now;

        var gil = ReadGil();
        if (gil < 0) return;

        var step = config.GilMilestoneStep;
        if (step <= 0) return;

        if (lastGil < 0)
        {
            // 登入後第一次讀到：只當基準，不觸發。否則每次上線都會誇一次。
            lastGil = gil;
            return;
        }

        var previousMilestone = lastGil / step;
        var currentMilestone = gil / step;
        lastGil = gil;

        if (currentMilestone > previousMilestone)
            service.TryTrigger(PraiseCategory.GilMilestone);
    }

    /// <summary>
    /// 讀目前的 Gil。讀不到回 <c>-1</c>。
    /// </summary>
    /// <remarks>
    /// 走 <c>InventoryManager.Instance()-&gt;GetGil()</c>。
    /// <para>
    /// 📌 兩條特徵碼都在台服 7.20 客戶端上離線驗過（2026-08-28，<c>tools/sigscan/verify_cs_sigs.py</c>，
    /// 校準閘門全 PASS）：
    /// <c>InventoryManager.Instance()</c> 的 <c>[StaticAddress("48 8D 0D ?? ?? ?? ?? 81 C2", 3)]</c>
    /// 唯一命中（<c>lea</c> 取靜態位址，<b>不是</b>指標解參，所以永遠不會是 null）；
    /// <c>GetGil()</c> 的 <c>[MemberFunction("E8 ?? ?? ?? ?? 3B 44 24 58")]</c> 也是唯一命中。
    /// </para>
    /// <para>
    /// ⚠️ 特徵碼失配時 FFXIVClientStructs 會擲 <c>InvalidOperationException</c>（在受控框架裡攔得到），
    /// 所以這裡的 <c>try/catch</c> 對「台服改版後 sig 死掉」是有效的防護。
    /// <b>但它擋不住 AccessViolation</b>——那在 .NET Core 是 corrupted-state exception，攔不到。
    /// 這也是為什麼要先離線驗特徵碼，而不是靠 try/catch 兜底。
    /// </para>
    /// <para>
    /// 🔴 失敗只發生一次就永久停用這條線（<see cref="gilReadBroken"/>）：每 5 秒重試一次沒有意義，
    /// 只會把實機 log 洗掉，而 log 是事後唯一的診斷來源。
    /// </para>
    /// </remarks>
    private unsafe long ReadGil()
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return -1;
            return manager->GetGil();
        }
        catch (Exception ex)
        {
            gilReadBroken = true;
            Svc.Log.Information(
                $"[TataruPraise] 讀取 Gil 失敗，Gil 里程碑觸發已停用（重載外掛才會再試）：{ex.Message}");
            return -1;
        }
    }
}
