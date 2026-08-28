using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;

namespace TataruPraise.Core;

/// <summary>
/// 對外的 IPC 端點（Dalamud 原生 <see cref="ICallGateProvider{T1, TRet}"/>，不引 ECommons）。
/// </summary>
/// <remarks>
/// 契約名逐字寫在 <see cref="IpcContract"/>。🔴 名字定了就不能改，改名＝呼叫端靜默斷線。
/// <para>
/// 🔴 每個回呼都自己吃掉例外：IPC 的實作是在<b>呼叫端的執行緒</b>上跑的，這裡漏出去的例外會炸到別人的外掛裡。
/// </para>
/// </remarks>
public sealed class IpcProvider : IDisposable
{
    private readonly ICallGateProvider<string, bool> speak;
    private readonly ICallGateProvider<string, bool> praise;
    private readonly ICallGateProvider<bool> isAvailable;

    /// <summary>
    /// 已經為哪些「未知情境」印過警告（每個情境只印一次）。
    /// </summary>
    /// <remarks>
    /// 🔴 呼叫端很可能在自己的迴圈裡叫 <c>Praise</c>，鍵名打錯就會每秒洗一行 log——
    /// 而記錄檔是使用者事後唯一的診斷來源。印一次就夠了：訊息本身講的是設定問題，不是狀態。
    /// <para>
    /// 🔴 這個集合會被<b>呼叫端的執行緒</b>碰到（IPC 的實作是在對方的執行緒上跑的），所以要上鎖。
    /// </para>
    /// </remarks>
    private readonly HashSet<string> warnedUnknownCategories = new(StringComparer.Ordinal);

    private readonly object warnGate = new();

    public IpcProvider(PraiseService service)
    {
        speak = Svc.PluginInterface.GetIpcProvider<string, bool>(IpcContract.Speak);
        praise = Svc.PluginInterface.GetIpcProvider<string, bool>(IpcContract.Praise);
        isAvailable = Svc.PluginInterface.GetIpcProvider<bool>(IpcContract.IsAvailable);

        speak.RegisterFunc(text =>
        {
            try
            {
                return service.Speak(text);
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] IPC Speak 失敗：{ex.Message}");
                return false;
            }
        });

        praise.RegisterFunc(category =>
        {
            try
            {
                // 🔴 未知情境要跟「有情境但這次沒出聲」分得開：兩者都回 false，
                //    但前者是呼叫端把鍵名打錯／使用者還沒建那個情境，永遠不會好。
                if (!service.HasCategory(category))
                {
                    WarnUnknownCategoryOnce(category);
                    return false;
                }

                return service.Praise(category);
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] IPC Praise 失敗：{ex.Message}");
                return false;
            }
        });

        isAvailable.RegisterFunc(() =>
        {
            try
            {
                return service.IsAvailable();
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] IPC IsAvailable 失敗：{ex.Message}");
                return false;
            }
        });

        Svc.Log.Information(
            $"[TataruPraise] IPC 已註冊：{IpcContract.Speak}、{IpcContract.Praise}、{IpcContract.IsAvailable}");
    }

    /// <summary>對一個沒見過的情境印一次 Information（之後同一個情境不再印）。</summary>
    private void WarnUnknownCategoryOnce(string? category)
    {
        var name = category ?? string.Empty;

        bool first;
        lock (warnGate) first = warnedUnknownCategories.Add(name);
        if (!first) return;

        Svc.Log.Information(
            $"[TataruPraise] IPC Praise 收到未知情境「{name}」，這次不出聲。"
            + "請在設定視窗的「誇獎池」分頁按「新增情境」建立同名情境並生句、合成語音。"
            + "（同一個情境只提醒這一次。）");
    }

    public void Dispose()
    {
        // 🔴 註銷不要有任何前置條件判斷。艦隊踩過「Dispose 裡的 IPC 沒防護」的坑，
        //    這裡的三個欄位都是建構子裡直接指派的，不會是 null；例外照樣個別吞掉。
        try { speak.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 Speak 失敗：{ex.Message}"); }
        try { praise.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 Praise 失敗：{ex.Message}"); }
        try { isAvailable.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 IsAvailable 失敗：{ex.Message}"); }
    }
}
