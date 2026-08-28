using System;
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

    public void Dispose()
    {
        // 🔴 註銷不要有任何前置條件判斷。艦隊踩過「Dispose 裡的 IPC 沒防護」的坑，
        //    這裡的三個欄位都是建構子裡直接指派的，不會是 null；例外照樣個別吞掉。
        try { speak.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 Speak 失敗：{ex.Message}"); }
        try { praise.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 Praise 失敗：{ex.Message}"); }
        try { isAvailable.UnregisterFunc(); } catch (Exception ex) { Svc.Log.Information($"[TataruPraise] 註銷 IsAvailable 失敗：{ex.Message}"); }
    }
}
