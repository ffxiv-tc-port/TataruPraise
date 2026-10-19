using System;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text.ReadOnly;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace TataruPraise.Core;

/// <summary>是哪一種準備確認提示框。</summary>
public enum ReadyCheckPromptKind
{
    /// <summary>不是準備確認的提示框。</summary>
    None = 0,

    /// <summary>別人發起：「準備確認開始了。準備好了嗎？…」。</summary>
    Received = 1,

    /// <summary>自己發起前的確認：「要對小隊或團隊全體人員進行準備確認嗎？」。</summary>
    Initiate = 2,
}

/// <summary>
/// 準備確認：是／否提示框（<c>SelectYesno</c>）一跳出來就念。
/// </summary>
/// <remarks>
/// 判準＝提示框第一個 AtkValue 的整句文字，跟遊戲資料表 <c>Addon</c> 的那一列<b>逐字</b>比；
/// 兩邊用同一支 <see cref="ComparableText(byte[])"/> 正規化。不寫死任何中文字面。
/// <para>
/// 🔴 只讀、只出聲：不按「準備完畢」、不保存任何原生指標（文字在回呼裡當場抄成字串）。
/// </para>
/// </remarks>
public sealed class ReadyCheckTrigger : IDisposable
{
    /// <summary>別人發起時跳的提示框文字（<c>Addon</c> 表）。</summary>
    internal const uint ReceivedPromptRow = 8133;

    /// <summary>自己發起前的確認框文字（<c>Addon</c> 表）。</summary>
    internal const uint InitiatePromptRow = 8135;

    private const string PromptAddon = "SelectYesno";

    private readonly Configuration config;
    private readonly PraiseService service;
    private readonly string receivedText;
    private readonly string initiateText;

    /// <summary>「開頭像、整句不像」只提醒一次，避免同一個比對問題洗版。</summary>
    private bool nearMissLogged;

    /// <summary>註銷監聽要等到下一個 tick 才生效，這之間來的回呼一律不理。</summary>
    private volatile bool disposed;

    public ReadyCheckTrigger(Configuration config, PraiseService service)
    {
        this.config = config;
        this.service = service;

        receivedText = LoadSheetText(ReceivedPromptRow);
        initiateText = LoadSheetText(InitiatePromptRow);

        Svc.Log.Information(
            $"[TataruPraise] 啟動：準備確認比對文字＝別人發起「{receivedText}」（Addon#{ReceivedPromptRow}）"
            + $"、自己發起「{initiateText}」（Addon#{InitiatePromptRow}）"
            + $"，內建觸發{(config.TriggerReadyCheck ? "開" : "關")}。");

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PromptAddon, OnPromptSetup);
    }

    public void Dispose()
    {
        disposed = true;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, PromptAddon, OnPromptSetup);
    }

    /// <summary>
    /// SeString 位元組 → 可比對的純文字：只留文字、丟掉巨集，換行一律拿掉，頭尾空白修掉。
    /// </summary>
    /// <remarks>
    /// 🔴 表裡的換行是 NewLine 巨集、畫面上可能是字面 LF——兩邊都拿掉才比得上，所以畫面與表走同一支。
    /// </remarks>
    internal static string ComparableText(byte[] seString)
        => new ReadOnlySeStringSpan(seString).ExtractText().ReplaceLineEndings(string.Empty).Trim();

    /// <summary>畫面文字是哪一種準備確認提示框。空字串的比對對象一律不算命中。</summary>
    internal static ReadyCheckPromptKind Classify(string screen, string received, string initiate)
    {
        if (screen.Length == 0) return ReadyCheckPromptKind.None;
        if (received.Length > 0 && string.Equals(screen, received, StringComparison.Ordinal))
            return ReadyCheckPromptKind.Received;
        if (initiate.Length > 0 && string.Equals(screen, initiate, StringComparison.Ordinal))
            return ReadyCheckPromptKind.Initiate;
        return ReadyCheckPromptKind.None;
    }

    /// <summary>讀不到（表沒有這一列、或 Lumina 擲例外）回空字串＝那一種永遠不命中。</summary>
    private static string LoadSheetText(uint rowId)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(rowId);
            if (row == null) return string.Empty;
            return ComparableText(row.Value.Text.Data.ToArray());
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[TataruPraise] 讀 Addon#{rowId} 失敗，這一種準備確認不會觸發：{ex.Message}");
            return string.Empty;
        }
    }

    private void OnPromptSetup(AddonEvent type, AddonArgs args)
    {
        if (disposed || !config.TriggerReadyCheck) return;
        if (receivedText.Length == 0 && initiateText.Length == 0) return;

        var screen = ReadPromptText(args.Addon.Address);
        if (screen == null) return;

        var kind = Classify(screen, receivedText, initiateText);
        if (kind == ReadyCheckPromptKind.None)
        {
            LogNearMissOnce(screen);
            return;
        }

        // 先記提示框時間再出聲：冷卻擋掉這一聲時，同一次準備確認的 IPC 那一聲也照樣要略過。
        service.ReadyCheck.NotePrompt(Environment.TickCount64);
        var played = service.TryTrigger(PraiseCategory.ReadyCheck, chanceOverride: 100);

        var label = kind == ReadyCheckPromptKind.Received
            ? $"別人發起的提示框（Addon#{ReceivedPromptRow}）"
            : $"自己發起前的確認框（Addon#{InitiatePromptRow}）";
        Svc.Log.Information(
            $"[TataruPraise] 準備確認：來源＝{label}，"
            + (played ? "已出聲或排進待播。" : "這次沒出聲（冷卻、總開關、情境開關或沒有語音）。"));
    }

    /// <summary>提示框第一個 AtkValue 的文字；不是字串或讀不到回 null。</summary>
    /// <remarks>
    /// 只在 PostSetup 回呼裡用：addon 在這個呼叫期間一定活著，位元組當場抄成 byte[]、不留指標。
    /// </remarks>
    private static unsafe string? ReadPromptText(nint address)
    {
        var addon = (AtkUnitBase*)address;
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0) return null;

        var first = addon->AtkValues[0];
        var baseType = first.Type & ValueType.TypeMask;
        if (baseType != ValueType.String && baseType != ValueType.String8) return null;
        if (first.String.Value == null) return null;

        var raw = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(first.String.Value);
        return ComparableText(raw.ToArray());
    }

    /// <summary>開頭跟別人發起的那句一樣、整句卻對不上：多半是改版改了字，提醒一次讓人看得到。</summary>
    private void LogNearMissOnce(string screen)
    {
        if (nearMissLogged || receivedText.Length < 4) return;
        if (!screen.StartsWith(receivedText[..4], StringComparison.Ordinal)) return;

        nearMissLogged = true;
        Svc.Log.Information(
            $"[TataruPraise] 準備確認：提示框「{screen}」開頭像、整句跟 Addon#{ReceivedPromptRow}「{receivedText}」不同，沒有觸發。");
    }
}

/// <summary>
/// 同一次準備確認只念一次：提示框出現時記時間，之後 <see cref="SameCheckMilliseconds"/> 內的 IPC 呼叫略過。
/// </summary>
/// <remarks>
/// 🔴 會被 IPC 呼叫端的執行緒碰到，時間戳一律走 <see cref="Interlocked"/>。
/// 時間用 <see cref="Environment.TickCount64"/>（單調），不用牆鐘。
/// </remarks>
public sealed class ReadyCheckGate
{
    /// <summary>同一次準備確認的時間窗：涵蓋按下回答與確認結束時打開的狀態一覽視窗。</summary>
    internal const long SameCheckMilliseconds = 60_000;

    private const long NoPrompt = long.MinValue;

    private long promptAtMs = NoPrompt;

    internal void NotePrompt(long nowMs) => Interlocked.Exchange(ref promptAtMs, nowMs);

    /// <summary>距上次提示框幾毫秒；這次載入還沒看過提示框回 -1。</summary>
    internal long MillisecondsSincePrompt(long nowMs)
    {
        var at = Interlocked.Read(ref promptAtMs);
        return at == NoPrompt ? -1 : nowMs - at;
    }

    internal static bool IsSameCheck(long elapsedMs) => elapsedMs >= 0 && elapsedMs < SameCheckMilliseconds;
}
