using System.Collections.Generic;

namespace TataruPraise.Core;

/// <summary>
/// 誇獎池的情境分類。
/// </summary>
/// <remarks>
/// 🔴 這些字串同時是 <c>pool.json</c> 的鍵、IPC <c>TataruPraise.Praise(category)</c> 的參數，
/// 以及 Gemini 生句時餵進去的情境描述來源——<b>改字面等於把既有使用者的整池對不上</b>。
/// <para>
/// 📌 <see cref="PraisePool"/> 讀檔時<b>不會丟掉不認得的鍵</b>：規格書裡列過「成就」「採集製作大成功」
/// 「連續登入」這些這一版還沒有觸發來源的情境，如果使用者自己加進 pool.json，存檔時會原樣寫回去。
/// </para>
/// </remarks>
public static class PraiseCategory
{
    public const string DutyComplete = "副本完成";
    public const string LevelUp = "升等";
    public const string Login = "登入";
    public const string GilMilestone = "Gil里程碑";

    /// <summary>這一版真的有觸發來源的情境，順序即 UI 上的顯示順序。</summary>
    public static readonly string[] All =
    [
        DutyComplete,
        LevelUp,
        Login,
        GilMilestone,
    ];

    /// <summary>餵給文字後端的情境描述（比分類名多一點上下文，生出來的句子才不會空泛）。</summary>
    public static readonly Dictionary<string, string> Situations = new()
    {
        [DutyComplete] = "前輩剛剛順利通關了一個副本",
        [LevelUp] = "前輩剛剛升等了",
        [Login] = "前輩剛登入遊戲，久違地上線了",
        [GilMilestone] = "前輩存的 Gil 剛跨過一個新的里程碑",
    };

    /// <summary>把情境名換成餵給文字後端的描述；不認得的分類就直接用分類名。</summary>
    public static string DescribeSituation(string category)
        => Situations.TryGetValue(category, out var s) ? s : $"前輩剛剛達成了「{category}」";
}
