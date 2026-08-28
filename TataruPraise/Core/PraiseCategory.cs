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
    };

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
