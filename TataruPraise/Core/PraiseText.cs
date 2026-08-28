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
