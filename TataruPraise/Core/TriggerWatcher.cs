using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;

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

    /// <summary>
    /// 已通關過的副本（ContentFinderCondition row id）；載入時從設定灌進來。
    /// </summary>
    /// <remarks>
    /// 📌 設定檔存 <see cref="System.Collections.Generic.List{T}"/>（JSON 好讀好手改），
    /// 執行期用 HashSet 比對——副本完成是低頻事件，但線性搜尋一份會長到幾百筆的清單沒有必要。
    /// </remarks>
    private readonly HashSet<uint> clearedDuties;

    /// <summary>ContentFinderCondition 反查壞掉過就不再試，也只寫一次 log。</summary>
    private bool contentLookupBroken;

    /// <summary>警示線的輪詢間隔。血量與周圍敵人不需要每幀掃，4 Hz 對「來得及喊一聲」綽綽有餘。</summary>
    private static readonly TimeSpan AlertPollInterval = TimeSpan.FromMilliseconds(250);

    private DateTime lastAlertPollUtc = DateTime.MinValue;

    /// <summary>
    /// 血量警示的遲滯：血量回到門檻以上才重新「上膛」。
    /// </summary>
    /// <remarks>
    /// 🔴 沒有遲滯的話，血量在門檻附近抖動時每一次輪詢都會觸發一次，冷卻一到就再喊一次。
    /// 上膛條件刻意是「回到門檻以上」而不是「離開戰鬥」——一場戰鬥裡掉血兩次是該喊兩次的。
    /// </remarks>
    private bool lowHpArmed = true;

    /// <summary>
    /// 上一次輪詢時，各敵對玩家離我多遠（<c>GameObjectId</c> → 距離）。
    /// </summary>
    /// <remarks>
    /// 🔴 只存 <b>id 與純量</b>。原生指標與 <see cref="Dalamud.Game.ClientState.Objects.Types.IGameObject"/>
    /// 包裝一律不跨幀保存：本 pin 的 ObjectTable 是每格重用同一個包裝、存取時就地改寫 Address，
    /// 跨幀持有會靜默換人或懸空。
    /// </remarks>
    private readonly Dictionary<ulong, float> lastEnemyDistance = [];

    /// <summary>這一輪掃描用的暫存（每次輪詢重用，不要每 250ms 配一個新字典）。</summary>
    private readonly Dictionary<ulong, float> enemyDistanceScratch = [];

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
        clearedDuties = new HashSet<uint>(config.ClearedDuties);

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

    /// <summary>
    /// 副本完成。<b>首次通關</b>走另一個機率。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>DutyCompleted</c> 給的是 <b>territory id</b>（Dalamud 直接轉發 <c>ClientState.TerritoryType</c>），
    /// 不是 ContentFinderCondition 的 id。要判「這個副本我通關過沒有」必須先反查：
    /// <c>TerritoryType</c> 表的 <c>ContentFinderCondition</c> 欄就是那個 RowRef。
    /// <para>
    /// 📌 台服 7.20 的資料離線對過（<c>exd-tc/7.20</c>）：territory 1039 → CFC 1「監獄廢墟托托·拉克千獄」，
    /// 而 CFC 1 的 <c>TerritoryType</c> 欄也回 1039，雙向一致。非副本場景（例如 territory 128 利姆薩）
    /// 的 <c>ContentFinderCondition</c> 是 0。
    /// </para>
    /// <para>
    /// 🔴 反查不到（回 0、或表根本讀不到）就<b>照一般副本處理</b>：走原機率、也不記進已通關集合。
    /// 查不到要走得下去，不可以崩、也不可以誤判成首通而每次都用 100%。
    /// </para>
    /// <para>
    /// ⚠️ 「已通關」是<b>這個外掛自己記的</b>，不是遊戲的通關紀錄——老角色第一次跑舊副本照樣算首通。
    /// 設定視窗的 tooltip 有寫清楚。
    /// </para>
    /// </remarks>
    private void OnDutyCompleted(object? sender, ushort territoryType)
    {
        if (!config.TriggerDutyComplete) return;

        var contentId = ResolveContentFinderCondition(territoryType, out var contentName);
        if (contentId == 0)
        {
            Svc.Log.Information(
                $"[TataruPraise] 副本完成：territory {territoryType} 反查不到 ContentFinderCondition，照一般副本處理。");
            service.TryTrigger(PraiseCategory.DutyComplete);
            return;
        }

        // 🔴 先記「通關過了」再擲骰：這件事跟有沒有出聲無關，機率沒中也不能讓它下次又算首通。
        var firstClear = clearedDuties.Add(contentId);
        if (firstClear)
        {
            config.ClearedDuties.Add(contentId);
            config.Save();
        }

        var chance = firstClear
            ? Math.Clamp(config.FirstClearChancePercent, 0, 100)
            : Math.Clamp(config.ChancePercent, 0, 100);

        var label = contentName.Length > 0 ? contentName : $"CFC {contentId}";
        Svc.Log.Information(
            $"[TataruPraise] 副本完成：{label}（CFC {contentId}）"
            + $"，{(firstClear ? "首次通關" : "先前通關過")}，這次用 {chance}% 機率。");

        service.TryTrigger(PraiseCategory.DutyComplete, chance);
    }

    /// <summary>
    /// territory id → ContentFinderCondition row id。查不到回 0。
    /// </summary>
    /// <remarks>
    /// 📌 純 Lumina 查表，沒有原生指標、沒有跨幀狀態，這裡的 <c>try/catch</c> 是有效的
    /// （表不存在／欄位對不上時 Lumina 擲的是一般的受控例外）。
    /// <para>
    /// 🔴 失敗只發生一次就永久停用反查（<see cref="contentLookupBroken"/>）：每次通關都重試一次
    /// 只會洗掉實機記錄檔，而記錄檔是事後唯一的診斷來源。停用之後所有副本都走一般機率。
    /// </para>
    /// </remarks>
    private uint ResolveContentFinderCondition(ushort territoryType, out string name)
    {
        name = string.Empty;
        if (contentLookupBroken) return 0;

        try
        {
            var row = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
            if (row == null) return 0;

            var reference = row.Value.ContentFinderCondition;
            if (reference.RowId == 0) return 0;

            name = reference.ValueNullable?.Name.ExtractText() ?? string.Empty;
            return reference.RowId;
        }
        catch (Exception ex)
        {
            contentLookupBroken = true;
            Svc.Log.Information(
                $"[TataruPraise] 反查 ContentFinderCondition 失敗，首次通關加權已停用（重載外掛才會再試）：{ex.Message}");
            return 0;
        }
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
        lowHpArmed = true;
        lastEnemyDistance.Clear();

        if (!config.TriggerLogin) return;

        if (!config.LoginOncePerDay)
        {
            service.TryTrigger(PraiseCategory.Login);
            return;
        }

        // 🔴 日期戳只在「真的出聲了」之後才寫。冷卻擋掉、機率沒中、池裡沒有已合成的句子——
        //    這些都不算今天誇過了，不然一次沒中就整天都不會有。
        var today = Configuration.TodayStamp();
        if (string.Equals(config.LastLoginPraiseDate, today, StringComparison.Ordinal))
        {
            Svc.Log.Information($"[TataruPraise] 登入誇獎：今天（{today}）已經誇過了，這次略過。");
            return;
        }

        if (!service.TryTrigger(PraiseCategory.Login)) return;

        config.LastLoginPraiseDate = today;
        config.Save();
    }

    private void OnLogout(int type, int code)
    {
        knownLevels.Clear();
        lastGil = -1;
        lowHpArmed = true;
        lastEnemyDistance.Clear();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now - lastAlertPollUtc >= AlertPollInterval)
        {
            lastAlertPollUtc = now;
            PollAlerts();
        }

        if (!config.TriggerGilMilestone) return;
        if (gilReadBroken) return;
        if (!Svc.ClientState.IsLoggedIn)
        {
            lastGil = -1;
            return;
        }

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
    /// <summary>
    /// 三條戰鬥警示線：血量低、被大量標記、背後有人。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>純讀狀態、純出聲，不做任何遊戲操作</b>——不施放、不走位、不改目標。
    /// <para>
    /// 🔴 整段只在 <see cref="OnFrameworkUpdate"/> 裡跑，物件包裝<b>當幀用完即丟</b>，
    /// 只把 <c>GameObjectId</c>（ulong）與距離（float）留到下一輪。
    /// </para>
    /// <para>
    /// ⚠️ 後兩條只在 PvP 區域跑（<see cref="Dalamud.Plugin.Services.IClientState.IsPvP"/>）：
    /// PvE 的敵人不是玩家，「被幾個人標記」與「有人繞後」在那裡沒有意義，而且會白掃物件表。
    /// </para>
    /// </remarks>
    private void PollAlerts()
    {
        if (!config.TriggerLowHp && !config.TriggerMarkedByMany && !config.TriggerEnemyBehind)
        {
            if (lastEnemyDistance.Count > 0) lastEnemyDistance.Clear();
            return;
        }

        // 📌 走 IObjectTable.LocalPlayer：IClientState.LocalPlayer 在本 pin 已標記過時。
        var me = Svc.Objects.LocalPlayer;
        if (me == null)
        {
            lowHpArmed = true;
            lastEnemyDistance.Clear();
            return;
        }

        // 🔴 純量在這裡一次抄乾淨，後面完全不再碰 me（包裝不跨迴圈用）。
        var myId = me.GameObjectId;
        var myPosition = me.Position;
        var myRotation = me.Rotation;
        var currentHp = me.CurrentHp;
        var maxHp = me.MaxHp;

        if (config.TriggerLowHp) PollLowHp(currentHp, maxHp);

        if (!Svc.ClientState.IsPvP)
        {
            // 離開 PvP 就把上一輪的距離忘掉：留著會讓下次進場的第一輪誤判成「正在接近」。
            if (lastEnemyDistance.Count > 0) lastEnemyDistance.Clear();
            return;
        }

        if (!config.TriggerMarkedByMany && !config.TriggerEnemyBehind) return;
        PollHostilePlayers(myId, myPosition, myRotation);
    }

    /// <summary>
    /// 血量低。
    /// </summary>
    /// <remarks>
    /// 判定式：<c>InCombat</c> 且 <c>CurrentHp * 100 / MaxHp &lt; 門檻</c>，而且上一次觸發之後
    /// 血量曾經回到門檻以上（<see cref="lowHpArmed"/>）。
    /// <para>📌 用整數百分比比較，不用浮點——避免「29.999% 算不算跌破」這種邊界問題。</para>
    /// </remarks>
    private void PollLowHp(uint currentHp, uint maxHp)
    {
        if (maxHp == 0) return;

        var percent = (int)(currentHp * 100UL / maxHp);
        var threshold = Math.Clamp(config.LowHpThresholdPercent, 1, 99);

        if (percent >= threshold)
        {
            lowHpArmed = true;
            return;
        }

        if (!lowHpArmed) return;
        if (currentHp == 0) return;
        if (!Svc.Condition[ConditionFlag.InCombat]) return;

        lowHpArmed = false;
        Svc.Log.Information($"[TataruPraise] 警示：血量 {percent}%（門檻 {threshold}%）。");

        // 🔴 警示不擲骰：30% 機率的警示比沒有還糟（該喊的時候不喊）。冷卻照走。
        service.TryTrigger(PraiseCategory.LowHp, 100);
    }

    /// <summary>
    /// 掃一次物件表，同時算「被幾個敵對玩家鎖定」與「幾個敵對玩家從背後接近」。
    /// </summary>
    /// <remarks>
    /// 🔴 兩條線<b>共用同一次掃描</b>：物件表最多 200 格，每 250ms 掃兩次沒有必要。
    /// <para>
    /// 敵對判定＝<see cref="Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile"/>
    /// （Dalamud 由 <c>Character.IsHostile</c> 轉出來的）。⚠️ 這個旗標在台服 PvP 裡對敵方玩家是不是
    /// 一定會亮，<b>離線證不了</b>——證不了的後果是「這兩條線不觸發」，不是崩潰。
    /// </para>
    /// <para>
    /// 「從背後接近」的判定式（三個條件同時成立）：
    /// <list type="number">
    /// <item>在背後：以我的 <c>Rotation</c> 為前方，前方向量 <c>(sin r, cos r)</c> 與
    /// 「我→他」的水平向量內積 &lt; 0，等價於夾角大於 90°。</item>
    /// <item>夠近：水平距離 ≤ 設定的碼數。</item>
    /// <item>正在接近：這一輪的距離比<b>上一輪</b>小（至少 0.05 碼，濾掉抖動）。
    /// 上一輪沒看過這個 id 就<b>不算</b>——剛進視野的第一輪沒有比較基準。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void PollHostilePlayers(ulong myId, Vector3 myPosition, float myRotation)
    {
        var forwardX = MathF.Sin(myRotation);
        var forwardZ = MathF.Cos(myRotation);

        var range = Math.Clamp(config.EnemyBehindRange, 1f, 50f);
        var markedCount = 0;
        var behindCount = 0;

        enemyDistanceScratch.Clear();

        foreach (var obj in Svc.Objects)
        {
            // 🔴 obj 只在這一圈裡用，絕不留到下一幀。
            if (obj is not IPlayerCharacter player) continue;

            var id = player.GameObjectId;
            if (id == myId) continue;
            if ((player.StatusFlags & StatusFlags.Hostile) == 0) continue;

            if (player.TargetObjectId == myId) markedCount++;

            var dx = player.Position.X - myPosition.X;
            var dz = player.Position.Z - myPosition.Z;
            var distance = MathF.Sqrt((dx * dx) + (dz * dz));
            enemyDistanceScratch[id] = distance;

            if (!config.TriggerEnemyBehind) continue;
            if (distance > range) continue;

            // 內積 < 0 ＝ 他在我的後半平面。
            if ((dx * forwardX) + (dz * forwardZ) >= 0f) continue;
            if (!lastEnemyDistance.TryGetValue(id, out var previous)) continue;
            if (distance >= previous - 0.05f) continue;

            behindCount++;
        }

        lastEnemyDistance.Clear();
        foreach (var (id, distance) in enemyDistanceScratch) lastEnemyDistance[id] = distance;

        if (config.TriggerMarkedByMany)
        {
            var need = Math.Max(1, config.MarkedByManyCount);
            if (markedCount >= need)
            {
                Svc.Log.Information($"[TataruPraise] 警示：{markedCount} 個敵對玩家鎖定我（門檻 {need}）。");
                service.TryTrigger(PraiseCategory.MarkedByMany, 100);
            }
        }

        if (!config.TriggerEnemyBehind) return;

        var behindNeed = Math.Max(1, config.EnemyBehindCount);
        if (behindCount < behindNeed) return;

        Svc.Log.Information(
            $"[TataruPraise] 警示：{behindCount} 個敵對玩家從背後接近（門檻 {behindNeed}、{range:F0} 碼內）。");
        service.TryTrigger(PraiseCategory.EnemyBehind, 100);
    }

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
