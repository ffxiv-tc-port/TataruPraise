using System;
using System.Collections.Generic;

namespace TataruPraise.Core;

/// <summary>
/// 誇獎池的情境分類。
/// </summary>
/// <remarks>
/// 🔴 這些字串同時是 <c>pool.json</c> 的鍵、IPC <c>TataruPraise.Praise(category)</c> 的參數，
/// 以及 Gemini 生句時餵進去的情境描述來源——<b>改字面等於把既有使用者的整池對不上</b>。
/// <para>
/// 🔴 <see cref="Submarine"/>、<see cref="Crafting"/>、<see cref="Cosmic"/> 這三個是給<b>別的外掛</b>
/// 透過 IPC 呼叫用的（AutoRetainer 潛艇回港、Artisan 清單製作完成、ICE 宇宙探索金評）。
/// 呼叫端是拿字面字串來叫的，<b>鍵名逐字固定，一個字都不能改</b>。
/// </para>
/// <para>
/// 📌 <see cref="PraisePool"/> 讀檔時<b>不會丟掉不認得的鍵</b>：使用者自己在設定視窗加的情境、
/// 或手動寫進 pool.json 的鍵，存檔時會原樣寫回去。
/// </para>
/// </remarks>
public static class PraiseCategory
{
    public const string DutyComplete = "副本完成";
    public const string LevelUp = "升等";
    public const string Login = "登入";
    public const string GilMilestone = "Gil里程碑";

    /// <summary>AutoRetainer：潛水艇整隊回港／僱員探險全部收完。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Submarine = "潛艇";

    /// <summary>Artisan：整份製作清單做完。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Crafting = "製作";

    /// <summary>ICE：宇宙探索任務拿到金評價。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Cosmic = "宇宙";

    /// <summary>戰鬥警示：自己的血量掉到門檻以下。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string LowHp = "血量低";

    /// <summary>戰鬥警示：同時被多個敵對玩家鎖定（PvP）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string MarkedByMany = "被大量敵人標記";

    /// <summary>戰鬥警示：有敵對玩家從背後接近（PvP）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string EnemyBehind = "敵人從後面來";

    /// <summary>提醒：任務／戰鬥開始（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string DutyStart = "任務開始";

    /// <summary>提醒：出現準備確認（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string ReadyCheck = "準備確認";

    /// <summary>提醒：過場動畫結束（NotificationMaster 叫）。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string CutsceneEnd = "過場結束";

    /// <summary>通知：副本排到。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string DutyPop = "副本排到";

    /// <summary>通知：到旗標。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string FlagArrived = "到旗標";

    /// <summary>通知：私訊。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Tell = "私訊";

    /// <summary>通知：抵達。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Arrived = "抵達";

    /// <summary>通知：中獎。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string Jackpot = "中獎";

    /// <summary>通知：需要幫忙。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string NeedHelp = "需要幫忙";

    /// <summary>通知：玩家警示。<b>鍵名由 IPC 呼叫端逐字使用。</b></summary>
    public const string PlayerAlert = "玩家警示";

    /// <summary>
    /// 內建情境，順序即 UI 上的顯示順序。
    /// </summary>
    /// <remarks>
    /// 🔴 內建情境<b>不可以在設定視窗刪掉</b>（刪了 <see cref="PraisePool.Load"/> 下次啟動又會補回來，
    /// 對使用者是「刪不掉」的鬼打牆）。自訂情境才有刪除鈕。
    /// <para>
    /// 📌 前四個有遊戲事件觸發來源；後三個沒有，靠別的外掛用 IPC 叫。
    /// </para>
    /// </remarks>
    public static readonly string[] All =
    [
        DutyComplete,
        LevelUp,
        Login,
        GilMilestone,
        Submarine,
        Crafting,
        Cosmic,
        LowHp,
        MarkedByMany,
        EnemyBehind,
        DutyStart,
        ReadyCheck,
        CutsceneEnd,
        DutyPop,
        FlagArrived,
        Tell,
        Arrived,
        Jackpot,
        NeedHelp,
        PlayerAlert,
    ];

    /// <summary>內建情境的預設「情境描述」（餵給文字後端，比分類名多一點上下文）。</summary>
    /// <remarks>
    /// 📌 使用者可以在設定視窗改寫，改寫後的值存在 <see cref="Configuration.CategoryDescriptions"/>，
    /// <b>不會</b>動到這裡。這裡是「沒有自訂描述時用的預設」。
    /// </remarks>
    public static readonly Dictionary<string, string> Situations = new()
    {
        [DutyComplete] = "前輩剛剛順利通關了一個副本",
        [LevelUp] = "前輩剛剛升等了",
        [Login] = "前輩剛登入遊戲，久違地上線了",
        [GilMilestone] = "前輩存的 Gil 剛跨過一個新的里程碑",
        [Submarine] = "這是通知：前輩派出去的潛水艇整隊平安回港了（或僱員的探險全部收完了）。請用一句 8~15 字的短句，先說明這件事，再簡短稱讚一下；不要鋪陳前情、不要多講第二件事。",
        [Crafting] = "這是通知：前輩把整份製作清單做完了。請用一句 8~15 字的短句，先說明這件事，再簡短稱讚一下；不要鋪陳前情、不要多講第二件事。",
        [Cosmic] = "這是通知：前輩在宇宙探索的任務拿到了金評價。請用一句 8~15 字的短句，先說明這件事，再簡短稱讚一下；不要鋪陳前情、不要多講第二件事。",
        [LowHp] = "這是戰鬥警示，不是誇獎：前輩的血量掉到危險線以下了，正在戰鬥中。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [MarkedByMany] = "這是戰鬥警示，不是誇獎：好幾個敵對玩家同時鎖定了前輩。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [EnemyBehind] = "這是戰鬥警示，不是誇獎：有敵對玩家從前輩的背後接近。只輸出一句 2~6 字的極短句，像喊出來的一樣；不要稱讚、不要說明、不要鋪陳。",
        [DutyStart] = "這是提醒，不是誇獎：任務／戰鬥開始了。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [ReadyCheck] = "這是提醒，不是誇獎：跳出了準備確認，前輩要按確認。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [CutsceneEnd] = "這是提醒，不是誇獎：過場動畫結束了，要開打了。只輸出 2~6 字的極短句（最多 8 字），像喊出來的一樣；不要說明、不要鋪陳。",
        [DutyPop] = "這是通知，不是誇獎：副本配對排到了，要按確認才進得去。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [FlagArrived] = "這是通知，不是誇獎：前輩走到地圖上的旗標位置了。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Tell] = "這是通知，不是誇獎：有人傳了私訊給前輩。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Arrived] = "這是通知，不是誇獎：前輩抵達了目的地（傳送、乘騎或跑路結束）。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [Jackpot] = "這是通知，不是誇獎：前輩中獎了（抽選、隨機獎勵之類）。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [NeedHelp] = "這是通知，不是誇獎：自動化卡住了，需要前輩過來看一下。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
        [PlayerAlert] = "這是通知，不是誇獎：附近出現了要注意的玩家。只輸出 2~10 字的極短句，像喊出來的一樣；不要說明、不要鋪陳。",
    };

    /// <summary>
    /// 內建情境的「句長上限覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 三個 IPC 通知情境（潛艇／製作／宇宙）要的是<b>短通知句</b>（8~15 字），
    /// 跟一般誇獎句（12~25 字）不是同一種東西。全域上限 28 對它們太鬆——
    /// 生出來的長句照樣入池，實機上就會變成「通知念了五秒才講完」。
    /// <para>
    /// 📌 沒列在這裡的情境（原本那四個、還有使用者自訂的）回 0，代表<b>用全域上限</b>。
    /// 使用者在設定視窗填的覆寫存在 <see cref="Configuration.CategoryMaxLength"/>，優先於這裡。
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, int> MaxLengths = new()
    {
        [Submarine] = 16,
        [Crafting] = 16,
        [Cosmic] = 16,
        [LowHp] = 8,
        [MarkedByMany] = 8,
        [EnemyBehind] = 8,
        [DutyStart] = 10,
        [ReadyCheck] = 10,
        [CutsceneEnd] = 10,
        [DutyPop] = 12,
        [FlagArrived] = 12,
        [Tell] = 12,
        [Arrived] = 12,
        [Jackpot] = 12,
        [NeedHelp] = 12,
        [PlayerAlert] = 12,
    };

    /// <summary>
    /// 內建情境的「句長<b>下限</b>覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 全域下限 <see cref="PraiseText.MinLength"/>（6 字）是拿來擋「模型吐出來的殘句」的，
    /// 對警示／提醒情境完全不適用——「後面！」只有 3 個字，正是我們要的東西。
    /// 不放寬下限的話，這幾個情境生回來的句子會<b>全部被當成殘句丟掉</b>，而且看起來像模型壞掉。
    /// </remarks>
    public static readonly Dictionary<string, int> MinLengths = new()
    {
        [LowHp] = 2,
        [MarkedByMany] = 2,
        [EnemyBehind] = 2,
        [DutyStart] = 2,
        [ReadyCheck] = 2,
        [CutsceneEnd] = 2,
        [DutyPop] = 2,
        [FlagArrived] = 2,
        [Tell] = 2,
        [Arrived] = 2,
        [Jackpot] = 2,
        [NeedHelp] = 2,
        [PlayerAlert] = 2,
    };

    /// <summary>
    /// 內建情境的「冷卻秒數覆寫」。
    /// </summary>
    /// <remarks>
    /// 🔴 全域冷卻（預設 120 秒）是為「偶爾誇一下」設計的，套到<b>通知</b>上會把東西吃掉：
    /// AutoRetainer 多角色連跑時，後面幾個角色的「潛艇」通知會全部落在冷卻裡靜默消失。
    /// 警示更不用說——過了兩分鐘才喊「後面！」沒有任何意義。
    /// <para>
    /// 📌 冷卻計時器是<b>逐情境</b>的（見 <see cref="PraiseService"/>）：「潛艇」的冷卻不會擋到「血量低」。
    /// 沒列在這裡的情境（原本那四個誇獎情境、還有自訂的）回 0，代表用全域冷卻。
    /// </para>
    /// </remarks>
    public static readonly Dictionary<string, int> Cooldowns = new()
    {
        [Submarine] = 5,
        [Crafting] = 5,
        [Cosmic] = 5,
        [LowHp] = 15,
        [MarkedByMany] = 10,
        [EnemyBehind] = 10,
        [DutyStart] = 5,
        [ReadyCheck] = 5,
        [CutsceneEnd] = 5,
        [DutyPop] = 5,
        [FlagArrived] = 5,
        [Tell] = 5,
        [Arrived] = 5,
        [Jackpot] = 5,
        [NeedHelp] = 5,
        [PlayerAlert] = 5,
    };

    /// <summary>內建的句長下限覆寫；沒有就回 0（＝用全域下限）。</summary>
    public static int DefaultMinLength(string category)
        => MinLengths.TryGetValue(category, out var n) ? n : 0;

    /// <summary>內建的冷卻秒數覆寫；沒有就回 0（＝用全域冷卻）。</summary>
    public static int DefaultCooldownSeconds(string category)
        => Cooldowns.TryGetValue(category, out var n) ? n : 0;

    /// <summary>內建的句長上限覆寫；沒有就回 0（＝用全域上限）。</summary>
    public static int DefaultMaxLength(string category)
        => MaxLengths.TryGetValue(category, out var n) ? n : 0;

    /// <summary>這個情境是不是內建的（內建的不可刪、而且一定有預設描述）。</summary>
    public static bool IsBuiltIn(string category) => Array.IndexOf(All, category) >= 0;

    /// <summary>內建情境的預設描述；不是內建情境就回空字串。</summary>
    public static string DefaultDescription(string category)
        => Situations.TryGetValue(category, out var s) ? s : string.Empty;

    /// <summary>
    /// 沒有任何描述時，退回用「鍵名」組出來的情境句。
    /// </summary>
    /// <remarks>
    /// 📌 這是<b>最後的退路</b>：使用者自己新增的情境如果沒填描述，就只能拿鍵名當線索。
    /// 真正的取用順序在 <see cref="Configuration.SituationOf"/>：自訂描述 → 內建預設描述 → 這裡。
    /// </remarks>
    public static string DescribeSituation(string category)
        => Situations.TryGetValue(category, out var s) ? s : $"前輩剛剛達成了「{category}」";
}
