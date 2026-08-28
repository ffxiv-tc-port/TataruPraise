using Dalamud.Configuration;

namespace TataruPraise;

/// <summary>文字後端模式。</summary>
/// <remarks>
/// ⚠️ 第一版<b>只實作 <see cref="Pool"/></b>。另外兩個值先佔位，讓設定檔的欄位語意固定下來，
/// 之後補做時不必再改一次設定結構（改列舉的數值＝既有使用者的設定靜默跑到別的模式去）。
/// 📌 列舉刻意從 0 開始且 0 就是預設值——沒有零值的列舉會讓 <c>default</c> 落在無效值上。
/// </remarks>
public enum TextBackend
{
    /// <summary>純池：執行期零 HTTP，只從本機誇獎池挑句、播事先合成好的快取。</summary>
    Pool = 0,

    /// <summary>雲端即時（Gemini）。TODO：尚未實作，選了等同 <see cref="Pool"/>。</summary>
    GeminiLive = 1,

    /// <summary>本機即時（Ollama）。TODO：尚未實作，選了等同 <see cref="Pool"/>。</summary>
    OllamaLive = 2,
}

public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // ── 總開關與節流 ───────────────────────────────────────────────
    /// <summary>總開關。🔴 預設關：安裝完不會突然有聲音。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>全域冷卻秒數。冷卻中的觸發直接丟棄，不排隊。</summary>
    public int CooldownSeconds { get; set; } = 120;

    /// <summary>觸發機率（%）。過了冷卻還要再擲一次骰。</summary>
    public int ChancePercent { get; set; } = 30;

    /// <summary>播放音量（0～1）。</summary>
    public float Volume { get; set; } = 0.8f;

    // ── 逐事件開關（全部預設關）─────────────────────────────────────
    public bool TriggerDutyComplete { get; set; } = false;
    public bool TriggerLevelUp { get; set; } = false;
    public bool TriggerLogin { get; set; } = false;
    public bool TriggerGilMilestone { get; set; } = false;

    /// <summary>Gil 里程碑的間隔：每跨過這個數字的整數倍就算一次里程碑。</summary>
    public long GilMilestoneStep { get; set; } = 1_000_000;

    // ── 語音橋接（GPT-SoVITS，預設同機）──────────────────────────────
    /// <summary>TTS 橋接位址。同機就是 127.0.0.1:9882；異機要填區網 IP 且對方要綁 0.0.0.0。</summary>
    public string TtsHost { get; set; } = "http://127.0.0.1:9882";

    /// <summary>聲線 id（橋接 <c>GET /speakers</c> 回的 <c>voice_id</c>）。</summary>
    public string VoiceId { get; set; } = "塔塔露";

    // ── 文字後端 ────────────────────────────────────────────────────
    public TextBackend Backend { get; set; } = TextBackend.Pool;

    /// <summary>Gemini API 金鑰。🔴 存在 Dalamud 的外掛設定檔裡，不進版控、不寫進 log。</summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>Gemini 模型名。可自填；<c>gemini-2.x-flash</c> 系列對新金鑰已停用，別填。</summary>
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>按一次「擴充誇獎池」時，每個情境要生幾句。</summary>
    public int GenerateCountPerCategory { get; set; } = 10;

    /// <summary>
    /// 句長上限（字，不含空白；中文標點算在內）。生成回來超過這個長度的句子直接丟掉。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>生成端</b>的閘門，只擋新句子；pool.json 裡既有的長句<b>不會</b>被它動到
    /// （那是使用者的資料）。要清掉舊的長句請按設定視窗裡的「移除超過上限的句子」。
    /// 📌 預設 28＝提示詞要求的 25 字再加幾格標點的餘裕。
    /// </remarks>
    public int MaxPraiseLength { get; set; } = Core.PraiseText.DefaultMaxLength;

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
