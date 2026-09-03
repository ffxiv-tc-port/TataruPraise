using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using TataruPraise.Core;
using TataruPraise.Windows;

namespace TataruPraise;

public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/tataru";

    public Configuration Config { get; }

    public PraisePool Pool { get; }

    public PraiseService Service { get; }

    public PoolJobs Jobs { get; }

    public WindowSystem WindowSystem { get; } = new("TataruPraise");

    /// <summary>試播的最後結果（設定視窗上顯示）。</summary>
    public string LastTestMessage { get; private set; } = string.Empty;

    private readonly ConfigWindow configWindow;
    private readonly TriggerWatcher triggers;
    private readonly DtrDisplay dtr;
    private readonly IpcProvider ipc;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        Config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Pool = new PraisePool();
        Pool.Load();
        if (Pool.SeedIfEmpty())
            Svc.Log.Information("[TataruPraise] 誇獎池是空的，已灌入內建的預設句。");

        Service = new PraiseService(Config, Pool);
        Jobs = new PoolJobs(Config, Pool);
        triggers = new TriggerWatcher(Config, Service);

        // 🔴 設定視窗要在 DTR 之前建好：那一格的右鍵會呼叫 ToggleConfig，
        //    先建視窗才不會有「格子已經掛上去、視窗還是 null」的空窗期。
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        dtr = new DtrDisplay(Config, Pool, Service, ToggleConfig);
        ipc = new IpcProvider(Service);

        Svc.PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        Svc.PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟塔塔露誇獎的設定視窗。/tataru test 試播一句。",
        });

        LogStartupState();
    }

    /// <summary>
    /// 啟動狀態。
    /// </summary>
    /// <remarks>
    /// 🔴 一律 <c>Information</c> 級：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒。
    /// 這一行回答的是事後看記錄檔時唯一推不出來的事——「總開關到底開著沒、有哪幾個觸發是開的、
    /// 池裡到底有沒有可播的東西」。<b>「有句子」與「有語音」是兩件事</b>，所以兩個數字都印。
    /// </remarks>
    private void LogStartupState()
    {
        var triggerNames = new System.Collections.Generic.List<string>();
        if (Config.TriggerDutyComplete) triggerNames.Add(PraiseCategory.DutyComplete);
        if (Config.TriggerLevelUp) triggerNames.Add(PraiseCategory.LevelUp);
        if (Config.TriggerLogin) triggerNames.Add(PraiseCategory.Login);
        if (Config.TriggerGilMilestone) triggerNames.Add(PraiseCategory.GilMilestone);
        if (Config.TriggerLowHp) triggerNames.Add(PraiseCategory.LowHp);
        if (Config.TriggerMarkedByMany) triggerNames.Add(PraiseCategory.MarkedByMany);
        if (Config.TriggerEnemyBehind) triggerNames.Add(PraiseCategory.EnemyBehind);
        if (Config.TriggerTellReceived) triggerNames.Add(PraiseCategory.TellReceived);
        if (Config.TriggerDutyPop) triggerNames.Add(PraiseCategory.DutyPop);
        if (Config.TriggerPartyInvite) triggerNames.Add(PraiseCategory.PartyInvite);
        if (Config.TriggerTradeRequest) triggerNames.Add(PraiseCategory.TradeRequest);

        var total = 0;
        var cached = 0;
        foreach (var category in PraiseCategory.All)
        {
            total += Pool.CountOf(category);
            cached += Pool.CachedCountOf(category);
        }

        Svc.Log.Information(
            $"[TataruPraise] 啟動：總開關 {(Config.Enabled ? "開" : "關")}"
            + $"，已啟用觸發 {(triggerNames.Count > 0 ? string.Join("、", triggerNames) : "（無）")}"
            + $"，冷卻 {Config.CooldownSeconds} 秒，機率 {Config.ChancePercent}%"
            + $"，誇獎池 {total} 句、其中 {cached} 句已有語音"
            + $"，橋接 {Config.TtsHost}，聲線 {Config.VoiceId}");

        // 🔴 addon 名對不上的失敗形狀是「不響、不崩、也沒有錯誤訊息」。
        //    把實際註冊的名字印出來，使用者回報「組隊邀請沒響」時才有東西可對。
        Svc.Log.Information(
            "[TataruPraise] 啟動：監聽的 addon＝"
            + $"組隊邀請「{TriggerWatcher.PartyInviteAddon}」、交易請求「{TriggerWatcher.TradeAddon}」"
            + "（每 250ms 輪詢可見性、取上升緣；名字對不上就是靜默不響，不會有錯誤訊息）。");

        if (total > 0 && cached == 0)
        {
            Svc.Log.Information(
                "[TataruPraise] 啟動：誇獎池裡一句語音都還沒合成，現在觸發也不會出聲。"
                + "請到設定視窗的「短句」分頁按「預合成全部」。");
        }
    }

    private void ToggleConfig() => configWindow.Toggle();

    /// <summary>試播一句，把結果留在 <see cref="LastTestMessage"/>。</summary>
    public void RunTest()
    {
        var ok = Service.PlayTest(out var message);
        LastTestMessage = ok ? $"試播：{message}" : message;
        Svc.Log.Information($"[TataruPraise] 試播：{(ok ? "成功" : "沒有播出")}　{message}");
    }

    /// <summary>試播<b>指定情境</b>的一句（設定視窗情境表格上那顆「試播」）。</summary>
    public void RunCategoryTest(string category)
    {
        var ok = Service.PlayCategoryTest(category, out var message);
        LastTestMessage = ok ? $"試播「{category}」：{message}" : message;
        Svc.Log.Information($"[TataruPraise] 試播「{category}」：{(ok ? "成功" : "沒有播出")}　{message}");
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            RunTest();
            return;
        }

        configWindow.Toggle();
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);

        Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        WindowSystem.RemoveAllWindows();

        // 🔴 順序：先把「還會再產生工作的東西」拆掉（觸發線、IPC、背景批次），最後才拆播放器。
        //    反過來的話，卸載當下正在跑的 Framework.Update 還可能碰到已經 Dispose 的音訊物件。
        triggers.Dispose();
        dtr.Dispose();
        ipc.Dispose();
        Jobs.Dispose();
        Service.Dispose();
    }
}
