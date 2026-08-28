using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TataruPraise.Core;

/// <summary>
/// 一次「擴充誇獎池」丟掉了哪些東西。
/// </summary>
/// <remarks>
/// 📌 給設定視窗的「上次結果」那一行用。使用者按完按鈕之後，唯一能判斷
/// 「是模型生得爛還是我的上限設得太緊」的地方就是這幾個數字，所以它必須看得見，不能只寫進記錄檔。
/// </remarks>
public sealed class GenerateStats
{
    /// <summary>超過句長上限被丟掉的句數。</summary>
    public int TooLong;

    /// <summary>太短被丟掉的句數。</summary>
    public int TooShort;

    /// <summary>沒有中文標點被丟掉的句數（送進橋接會念成怪腔）。</summary>
    public int NoPunctuation;

    /// <summary>與池裡既有句子重複、沒有入池的句數。</summary>
    public int Duplicate;

    /// <summary>有沒有任何被丟掉的東西。</summary>
    public bool AnyDropped => TooLong > 0 || TooShort > 0 || NoPunctuation > 0 || Duplicate > 0;

    /// <summary>畫在 UI 上的短句；沒丟掉任何東西就回空字串。</summary>
    public string Describe()
    {
        if (!AnyDropped) return string.Empty;

        var parts = new List<string>(4);
        if (TooLong > 0) parts.Add($"超長 {TooLong} 句");
        if (TooShort > 0) parts.Add($"過短 {TooShort} 句");
        if (NoPunctuation > 0) parts.Add($"沒標點 {NoPunctuation} 句");
        if (Duplicate > 0) parts.Add($"重複 {Duplicate} 句");
        return "丟棄 " + string.Join("、", parts);
    }
}

/// <summary>
/// Gemini：批量生誇獎句（只在按「擴充誇獎池」時才呼叫，執行期不用）。
/// </summary>
public static class GeminiClient
{
    /// <summary>
    /// 塔塔露人設系統提示。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>這段是接點規格書第 4 節的原文，已經 PoC 驗證過，逐字沿用、不要「潤飾」。</b>
    /// 尤其「務必保留自然的中文標點」那句：橋接是靠標點斷句的，句子沒標點會讓聲線越念越高變怪腔。
    /// <para>
    /// 🔴 SFW 紅線：塔塔露是拉拉菲爾族孩童體型，內容一律健全，絕不性化。最後一句的
    /// 「內容一律健全（SFW）」就是這條約束的落點。
    /// </para>
    /// </remarks>
    public const string SystemPrompt =
        "你是《FF14》破曉血盟的接待員兼財務總管塔塔露(Tataru Taru)，開朗、勤快、有點小得意、很熱心。" +
        "你稱呼使用者「前輩」。請用繁體中文(台灣)，依照使用者提供的遊戲情境，說一句誇獎前輩的話。" +
        "要求：只輸出一到兩句、口語、活潑；句尾偶爾用「的說/唷/呢」但別每句都加；" +
        "務必保留自然的中文標點(逗號、頓號、句號、驚嘆號)，讓語音好斷句；" +
        "不要 markdown、引號、旁白、表情符號、英文；內容一律健全(SFW)。只輸出誇獎本身。";

    /// <summary>
    /// 長度約束：接在人設之後，讓句子短而口語。
    /// </summary>
    /// <remarks>
    /// 🔴 這段是<b>追加</b>在 <see cref="SystemPrompt"/> 之後的，人設本文一個字都不能動。
    /// 實機回饋是「生出來的句子都偏長」——長句念起來像唸稿，也更容易踩到橋接的斷句問題。
    /// <para>
    /// ⚠️ 人設本文寫「只輸出一到兩句」，這裡收成「只能一句」。兩者字面上打架，所以這段開頭
    /// 明講「優先於上面的一到兩句」——模型看得懂，人來讀的時候也不會以為是漏改。
    /// </para>
    /// </remarks>
    public const string LengthPrompt =
        "額外的長度約束(優先於上面的一到兩句)："
        + "每句只能是一句話，12~25 個中文字，最多一個逗號；"
        + "要像順口講出來的一句話，不要堆疊多個理由、不要複述情境、不要接兩三個子句、不要鋪陳前情。";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 針對一個情境批量生句。失敗（含金鑰錯、額度用完、重試完還是失敗）回空清單，只寫 Information。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>429</c>（額度）與 <c>503</c>（過載）走<b>指數退避，最多重試 3 次</b>（4、8、16 秒）。
    /// 其他狀態碼（例如 <c>400</c> 金鑰錯、<c>404</c> 模型已下架）重試沒有意義，直接放棄。
    /// </remarks>
    public static async Task<List<string>> GenerateAsync(
        string apiKey, string model, string category, int count, int maxLength,
        CancellationToken token, GenerateStats? stats = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Svc.Log.Information("[TataruPraise] 沒有 Gemini 金鑰，跳過擴充。");
            return [];
        }

        var situation = PraiseCategory.DescribeSituation(category);
        var userText =
            $"遊戲情境：{situation}。" +
            $"請針對這個情境，一次產生 {count} 句彼此不重複的誇獎。" +
            "每一句都要能單獨拿出來用，句型、開頭、語氣都要不一樣，不要用同一個模板換詞；" +
            $"每句 12~25 個中文字，只能一句話、最多一個逗號，超過 {maxLength} 字的句子會被直接丟掉。" +
            "只輸出一個 JSON 陣列，陣列的每個元素是一個字串，就是一句誇獎；" +
            "不要輸出任何說明文字、不要 markdown 程式碼圍籬、不要鍵值物件。";

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt + LengthPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
            generationConfig = new { temperature = 1.0, maxOutputTokens = 4096 },
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        const int maxRetries = 3;
        var delaySeconds = 4;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(url, content, cts.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt == maxRetries)
                    {
                        Svc.Log.Information(
                            $"[TataruPraise] Gemini 回 HTTP {(int)response.StatusCode}，重試 {maxRetries} 次後放棄（情境：{category}）。");
                        return [];
                    }

                    Svc.Log.Information(
                        $"[TataruPraise] Gemini 回 HTTP {(int)response.StatusCode}，{delaySeconds} 秒後重試（第 {attempt + 1}/{maxRetries} 次，情境：{category}）。");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
                    delaySeconds *= 2;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 🔴 這裡刻意只印狀態碼，不印 url（url 裡帶著金鑰）。
                    Svc.Log.Information(
                        $"[TataruPraise] Gemini 失敗：HTTP {(int)response.StatusCode}（模型 {model}，情境：{category}）。"
                        + "400 多半是金鑰不對，404 多半是這個模型對新金鑰已停用。");
                    return [];
                }

                var text = ExtractText(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (text == null)
                {
                    Svc.Log.Information($"[TataruPraise] Gemini 回應裡沒有可用的文字（情境：{category}）。");
                    return [];
                }

                return ParseLines(text, maxLength, category, stats);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return [];
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] Gemini 失敗：{ex.Message}（情境：{category}）");
                return [];
            }
        }

        return [];
    }

    /// <summary>從回應 JSON 取出 <c>candidates[0].content.parts[*].text</c>。</summary>
    private static string? ExtractText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
            {
                return null;
            }

            var first = candidates[0];
            if (!first.TryGetProperty("content", out var contentEl)
                || !contentEl.TryGetProperty("parts", out var parts))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }

            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 解析 Gemini 回應失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把模型輸出解析成句子清單。
    /// </summary>
    /// <remarks>
    /// 📌 模型「應該」只回 JSON 陣列，但實務上常常包一層 <c>```json</c> 圍籬、或乾脆一行一句。
    /// 三段式退路：剝圍籬 → 試 JSON 陣列 → 都不行就逐行切並剝掉行首的編號／項目符號。
    /// 🔴 最後一定要把「沒有任何中文標點的句子」濾掉——那種句子送進橋接會念成怪腔。
    /// <para>
    /// 🔴 <b>提示詞只是請求，這裡的長度過濾才是保證。</b>模型照樣會吐出長句，超過
    /// <paramref name="maxLength"/> 字（不含空白）的直接丟掉，短於 <see cref="PraiseText.MinLength"/> 字的也丟
    /// （那多半是殘句）。丟掉幾句會寫進 <see cref="Svc.Log"/> 的 Information，也回填到
    /// <paramref name="stats"/> 讓設定視窗顯示得出來——<b>「被丟掉了」這件事不能只活在記錄檔裡</b>。
    /// </para>
    /// </remarks>
    public static List<string> ParseLines(
        string raw, int maxLength, string category = "", GenerateStats? stats = null)
    {
        var text = raw.Trim();

        // 剝 markdown 圍籬。
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) text = text[..fenceEnd];
            text = text.Trim();
        }

        var result = new List<string>();

        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String)
                            result.Add(el.GetString()!);
                    }
                }
            }
            catch (JsonException)
            {
                result.Clear();
            }
        }

        if (result.Count == 0)
        {
            foreach (var line in text.Split('\n'))
            {
                var cleaned = line.Trim().TrimStart('-', '*', '·', ' ', '\t');
                // 剝行首的 "1." / "1、" 之類編號。
                var dot = cleaned.IndexOfAny(['.', '、', ')', '）']);
                if (dot > 0 && dot <= 3)
                {
                    var head = cleaned[..dot];
                    var allDigits = true;
                    foreach (var c in head)
                    {
                        if (c is < '0' or > '9') { allDigits = false; break; }
                    }

                    if (allDigits) cleaned = cleaned[(dot + 1)..].Trim();
                }

                if (cleaned.Length > 0) result.Add(cleaned);
            }
        }

        var suffix = category.Length > 0 ? $"（情境：{category}）" : string.Empty;
        var filtered = new List<string>(result.Count);
        var tooLong = 0;
        var tooShort = 0;
        var noPunctuation = 0;

        foreach (var line in result)
        {
            // 頭尾的空白與引號在量長度之前就要剝掉，否則模型包一層「」會平白吃掉兩格額度。
            var s = PraiseText.Normalize(line);
            if (s.Length == 0) continue;

            var length = PraiseText.CountChars(s);
            if (length > maxLength)
            {
                tooLong++;
                continue;
            }

            if (length < PraiseText.MinLength)
            {
                tooShort++;
                continue;
            }

            if (!HasChinesePunctuation(s))
            {
                noPunctuation++;
                Svc.Log.Information($"[TataruPraise] 丟掉一句沒有中文標點的生成結果（會念成怪腔）：{s}");
                continue;
            }

            filtered.Add(s);
        }

        if (tooLong > 0)
            Svc.Log.Information($"[TataruPraise] 已丟棄 {tooLong} 句超長（超過 {maxLength} 字）{suffix}。");
        if (tooShort > 0)
            Svc.Log.Information($"[TataruPraise] 已丟棄 {tooShort} 句過短（不到 {PraiseText.MinLength} 字）{suffix}。");

        if (stats != null)
        {
            stats.TooLong += tooLong;
            stats.TooShort += tooShort;
            stats.NoPunctuation += noPunctuation;
        }

        return filtered;
    }

    /// <summary>句子裡有沒有自然的中文標點。</summary>
    public static bool HasChinesePunctuation(string s)
    {
        foreach (var c in s)
        {
            if (c is '，' or '、' or '。' or '！' or '？' or '；' or '：' or '~' or '～'
                or ',' or '.' or '!' or '?')
            {
                return true;
            }
        }

        return false;
    }
}
