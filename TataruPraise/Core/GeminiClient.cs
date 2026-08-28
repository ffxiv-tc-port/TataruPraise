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
        string apiKey, string model, string category, int count, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Svc.Log.Information("[TataruPraise] 沒有 Gemini 金鑰，跳過擴充。");
            return [];
        }

        var situation = PraiseCategory.DescribeSituation(category);
        var userText =
            $"遊戲情境：{situation}。" +
            $"請針對這個情境，一次產生 {count} 句彼此不重複的誇獎，用字和語氣都要有變化。" +
            "只輸出一個 JSON 陣列，陣列的每個元素是一個字串，就是一句誇獎；" +
            "不要輸出任何說明文字、不要 markdown 程式碼圍籬、不要鍵值物件。";

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
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

                return ParseLines(text);
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
    /// </remarks>
    public static List<string> ParseLines(string raw)
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

                cleaned = cleaned.Trim('"', '「', '」', ' ');
                if (cleaned.Length > 0) result.Add(cleaned);
            }
        }

        var filtered = new List<string>(result.Count);
        foreach (var line in result)
        {
            var s = line.Trim();
            if (s.Length is 0 or > 200) continue;
            if (!HasChinesePunctuation(s))
            {
                Svc.Log.Information($"[TataruPraise] 丟掉一句沒有中文標點的生成結果（會念成怪腔）：{s}");
                continue;
            }

            filtered.Add(s);
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
