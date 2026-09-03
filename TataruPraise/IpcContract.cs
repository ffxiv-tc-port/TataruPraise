namespace TataruPraise;

/// <summary>
/// 對外的 IPC 契約名稱。
/// </summary>
/// <remarks>
/// 🔴 <b>這三個字串一旦發版就不能改。</b> Dalamud 的 CallGate 是純字串比對，改名不會有任何錯誤訊息，
/// 呼叫端只會拿到「這個頻道沒有人註冊」而永遠得到預設值——<b>靜默斷線</b>。
/// 要換語意就開新的名字，舊的留著轉呼叫，不要就地改名。
/// <para>
/// 呼叫端範例（其他外掛）：
/// <code>
/// var speak = pluginInterface.GetIpcSubscriber&lt;string, bool&gt;("TataruPraise.Speak");
/// speak.InvokeFunc("前輩，你好厲害唷！");
/// </code>
/// 對方沒安裝／沒載入時 <c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，呼叫端自己要 try/catch。
/// </para>
/// </remarks>
public static class IpcContract
{
    /// <summary>直接念出指定的句子。<c>Func&lt;string, bool&gt;</c>，回傳「有沒有排進播放」。</summary>
    public const string Speak = "TataruPraise.Speak";

    /// <summary>
    /// 從指定情境的誇獎池挑一句念。<c>Func&lt;string, bool&gt;</c>，回傳「有沒有排進播放」。
    /// 📌 使用者可以在設定視窗<b>單獨關掉某個情境</b>；關掉時這裡回 <c>false</c>（不出聲，也不算錯誤）。
    /// </summary>
    public const string Praise = "TataruPraise.Praise";

    /// <summary>現在有沒有辦法出聲（總開關開著、而且有可播的內容）。<c>Func&lt;bool&gt;</c>。</summary>
    public const string IsAvailable = "TataruPraise.IsAvailable";

    /// <summary>
    /// <b>指定情境</b>現在有沒有辦法出聲。<c>Func&lt;string, bool&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 這是<b>新的頻道名</b>，不是把 <see cref="IsAvailable"/> 的語意就地改掉——
    /// 舊名字的「全域」語意有既有呼叫端在用，改語意會讓它們靜默換行為。
    /// <para>
    /// 📌 <see cref="IsAvailable"/> 回 true 只代表「整池<b>有某個情境</b>播得出來」。
    /// 呼叫端典型寫法是 <c>if(!IsAvailable()) return; Praise(cat);</c>，
    /// 於是「別的情境有語音、我這個情境一句都沒有」時<b>照樣通過</b>，
    /// 然後 <c>Praise</c> 回 false——呼叫端分不出「不能出聲」與「這次沒出聲」。
    /// 這個端點就是拿來補那個洞的。
    /// </para>
    /// <para>
    /// 判斷內容：總開關開著 ＋ 這個情境沒有被使用者關掉 ＋ 這個情境至少有一句已合成語音。
    /// <b>不</b>看冷卻——冷卻是「這次剛好不出聲」，不是「不能出聲」。
    /// </para>
    /// <para>
    /// ⚠️ 對方沒安裝／舊版沒有這個端點時 <c>InvokeFunc</c> 會擲 <c>IpcNotReadyError</c>，
    /// 跟其他端點一樣要呼叫端自己 try/catch。<b>catch 之後要當成「不能出聲」處理</b>，
    /// 不要退回去叫 <see cref="IsAvailable"/>（那樣就把這個端點的意義抵銷掉了）。
    /// </para>
    /// </remarks>
    public const string IsAvailableFor = "TataruPraise.IsAvailableFor";
}
