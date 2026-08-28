namespace TataruPraise.Core;

/// <summary>
/// 誇獎句的長度與清理規則（生成端、預設池、UI 三邊共用同一把尺）。
/// </summary>
/// <remarks>
/// 🔴 <b>長度一律用「不含空白的字元數」量</b>，包含中文標點在內。
/// 三個地方都要用同一個 <see cref="CountChars"/>：模型生出來的句子怎麼濾、UI 說「有幾句超過上限」、
/// 「移除超過上限的句子」實際刪哪幾句——用兩把不同的尺會讓「顯示 3 句超長、按下去卻刪了 5 句」。
/// <para>
/// 📌 提示詞裡對模型說的是「12~25 個中文字」，而預設上限 <see cref="DefaultMaxLength"/> 是 28：
/// 差的那幾格留給標點。上限是<b>硬牆</b>（超過就丟），不是目標值。
/// </para>
/// </remarks>
public static class PraiseText
{
    /// <summary>句長上限的預設值（字，不含空白）。</summary>
    public const int DefaultMaxLength = 28;

    /// <summary>句長下限（字，不含空白）。比這還短的多半是模型吐出來的殘句。</summary>
    public const int MinLength = 6;

    /// <summary>UI 滑桿的下界。</summary>
    public const int SliderMin = 12;

    /// <summary>UI 滑桿的上界。</summary>
    public const int SliderMax = 60;

    /// <summary>「短通知句」與「一般誇獎句」的分界：有效上限不超過這個數字就算短通知句。</summary>
    public const int ShortNoticeThreshold = 20;

    /// <summary>
    /// 依「有效句長上限」算出要寫進提示詞的目標字數範圍。
    /// </summary>
    /// <remarks>
    /// 🔴 提示詞裡的目標範圍<b>必須跟硬過濾的上限一致</b>。原本提示詞寫死「12~25 字」，
    /// 情境的上限被覆寫成 16 之後就會變成「請生 12~25 字，然後把超過 16 字的全丟掉」——
    /// 失敗形狀是<b>良率掉到接近 0，而且看起來像模型壞了</b>。
    /// <para>
    /// 📌 兩個錨點（改這個算式時要重新對）：
    /// 上限 28（全域預設）→ 12~25，跟改成可覆寫之前的提示詞逐字相同；
    /// 上限 16（潛艇／製作／宇宙）→ 8~15，就是短通知句要的尺寸。
    /// </para>
    /// <para>
    /// ⚠️ 兩段式（≤20 與 &gt;20）留給標點的餘裕不一樣（1 格 vs 3 格）：短句只有一個結尾標點，
    /// 長句常常還有一個逗號。這不是連續函數，20/21 的交界會跳一下，但兩個錨點都要落在對的值上。
    /// </para>
    /// </remarks>
    public static (int Min, int Max) LengthHint(int effectiveMaxLength)
    {
        var shortNotice = effectiveMaxLength <= ShortNoticeThreshold;
        var max = effectiveMaxLength - (shortNotice ? 1 : 3);
        if (max < MinLength + 1) max = MinLength + 1;

        var min = max - (shortNotice ? 7 : 13);
        if (min < MinLength) min = MinLength;
        return (min, max);
    }

    /// <summary>頭尾要剝掉的引號類字元（模型很愛自己包一層）。</summary>
    private static readonly char[] QuoteChars =
    [
        '"', '\'', '`',
        '「', '」', '『', '』', '“', '”', '‘', '’', '《', '》', '〈', '〉',
    ];

    /// <summary>句子的長度：不含空白的字元數（中文標點算在內）。</summary>
    public static int CountChars(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;

        var n = 0;
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c)) n++;
        }

        return n;
    }

    /// <summary>
    /// 去頭尾空白與引號。
    /// </summary>
    /// <remarks>
    /// 剝一層之後再 <c>Trim()</c> 一次再剝一層：模型常吐出 <c>「 …… 」</c> 這種引號與空白交錯的形狀。
    /// 迴圈有上限，避免病態輸入把這裡變成長迴圈。
    /// </remarks>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var text = s.Trim();
        for (var i = 0; i < 4; i++)
        {
            var stripped = text.Trim(QuoteChars).Trim();
            if (stripped.Length == text.Length) break;
            text = stripped;
        }

        return text;
    }

    /// <summary>句子有沒有超過上限。</summary>
    public static bool IsTooLong(string text, int maxLength) => CountChars(text) > maxLength;
}
