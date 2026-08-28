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

    /// <summary>從指定情境的誇獎池挑一句念。<c>Func&lt;string, bool&gt;</c>，回傳「有沒有排進播放」。</summary>
    public const string Praise = "TataruPraise.Praise";

    /// <summary>現在有沒有辦法出聲（總開關開著、而且有可播的內容）。<c>Func&lt;bool&gt;</c>。</summary>
    public const string IsAvailable = "TataruPraise.IsAvailable";
}
