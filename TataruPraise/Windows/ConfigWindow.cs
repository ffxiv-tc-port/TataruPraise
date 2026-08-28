using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using TataruPraise.Core;

namespace TataruPraise.Windows;

/// <summary>
/// 設定視窗。
/// </summary>
/// <remarks>
/// UI 原則（艦隊慣例）：
/// <list type="bullet">
/// <item>「隨時掃視」的資訊放列上，「起疑才查」的放 tooltip。</item>
/// <item>🔴 但<b>「不知道」本身要在列上看得見</b>——橋接沒查過就寫「尚未查詢」，查不到就寫「未連線」，
/// 絕不畫成看起來正常的樣子。</item>
/// </list>
/// </remarks>
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 ColorOk = new(0.36f, 0.83f, 0.45f, 1f);
    private static readonly Vector4 ColorBad = new(0.93f, 0.42f, 0.38f, 1f);
    private static readonly Vector4 ColorUnknown = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Plugin plugin;

    /// <summary>橋接查詢狀態。<see cref="SpeakerProbeState.NotProbed"/> 與「查了但沒有」是兩件事。</summary>
    private enum SpeakerProbeState
    {
        NotProbed,
        Probing,
        Ok,
        Failed,
    }

    private SpeakerProbeState probeState = SpeakerProbeState.NotProbed;
    private List<SpeakerInfo> speakers = [];
    private string probeMessage = string.Empty;

    public ConfigWindow(Plugin plugin) : base("塔塔露誇獎###TataruPraiseConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private Configuration Config => plugin.Config;

    public override void Draw()
    {
        DrawMasterSwitch();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##TataruPraiseTabs"))
        {
            if (ImGui.BeginTabItem("觸發###tab-trigger"))
            {
                DrawTriggerTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("語音###tab-voice"))
            {
                DrawVoiceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("誇獎池###tab-pool"))
            {
                DrawPoolTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawMasterSwitch()
    {
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("啟用（總開關）", ref enabled))
        {
            Config.Enabled = enabled;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("關掉的時候，遊戲事件、IPC 呼叫一律不出聲。下面的「試播一句」不受總開關限制。");

        ImGui.SameLine();
        var remaining = plugin.Service.CooldownRemainingSeconds;
        if (remaining > 0)
            ImGui.TextColored(ColorUnknown, $"（冷卻中，還有 {remaining:F0} 秒）");
        else if (plugin.Service.Audio.IsBusy)
            ImGui.TextColored(ColorUnknown, "（正在播放）");
        else
            ImGui.TextDisabled("（隨時可以出聲）");
    }

    private void DrawTriggerTab()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("每個觸發都是獨立開關，預設全部關閉。命中之後還要過全域冷卻與機率。");
        ImGui.Spacing();

        var duty = Config.TriggerDutyComplete;
        if (ImGui.Checkbox("副本完成", ref duty)) { Config.TriggerDutyComplete = duty; Config.Save(); }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("來源是 Dalamud 的 IDutyState.DutyCompleted，不是聊天訊息比對。");

        var level = Config.TriggerLevelUp;
        if (ImGui.Checkbox("升等", ref level)) { Config.TriggerLevelUp = level; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "只在等級真的往上跳的時候才算。\n"
                + "登入時客戶端會回報一次目前等級、切職業也會回報新職業的等級，那兩種都不會觸發。");
        }

        var login = Config.TriggerLogin;
        if (ImGui.Checkbox("登入", ref login)) { Config.TriggerLogin = login; Config.Save(); }

        var gil = Config.TriggerGilMilestone;
        if (ImGui.Checkbox("Gil 里程碑", ref gil)) { Config.TriggerGilMilestone = gil; Config.Save(); }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "每 5 秒讀一次身上的 Gil，跨過設定的整數倍才觸發。\n"
                + "登入後第一次讀到的數字只當基準，不會觸發。");
        }

        ImGui.Indent();
        var step = (int)Math.Clamp(Config.GilMilestoneStep, 10_000, int.MaxValue);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputInt("每跨過多少 Gil 算一次##gilStep", ref step, 100_000, 1_000_000))
        {
            Config.GilMilestoneStep = Math.Clamp(step, 10_000, int.MaxValue);
            Config.Save();
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var cooldown = Config.CooldownSeconds;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt("全域冷卻（秒）", ref cooldown, 0, 900))
        {
            Config.CooldownSeconds = cooldown;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("冷卻中的觸發直接丟棄，不會排隊等冷卻結束後補播。");

        var chance = Config.ChancePercent;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderInt("觸發機率（%）", ref chance, 0, 100))
        {
            Config.ChancePercent = chance;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("過了冷卻之後再擲一次骰。設 0 等於全部關掉。");
    }

    private void DrawVoiceTab()
    {
        ImGui.Spacing();

        var host = Config.TtsHost;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("橋接位址##ttsHost", ref host, 256))
        {
            Config.TtsHost = host;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "本機 GPT-SoVITS 橋接（gsv_bridge）的位址，預設 http://127.0.0.1:9882。\n"
                + "如果橋接跑在另一台機器，那邊要綁 0.0.0.0 並開防火牆，這裡填區網 IP。\n"
                + "連不上的時候外掛只是不出聲，不會卡遊戲。");
        }

        ImGui.SameLine();
        if (ImGui.Button("測試連線##probe"))
            ProbeSpeakers();

        // 🔴 狀態畫在列上，不藏 tooltip：「不知道」跟「壞了」都要一眼看得出來。
        switch (probeState)
        {
            case SpeakerProbeState.NotProbed:
                ImGui.TextColored(ColorUnknown, "橋接狀態：尚未查詢（按上面的「測試連線」）");
                break;
            case SpeakerProbeState.Probing:
                ImGui.TextColored(ColorUnknown, "橋接狀態：查詢中…");
                break;
            case SpeakerProbeState.Ok:
                ImGui.TextColored(ColorOk, $"橋接狀態：已連線，{speakers.Count} 個聲線");
                break;
            case SpeakerProbeState.Failed:
                ImGui.TextColored(ColorBad, "橋接狀態：未連線");
                if (ImGui.IsItemHovered() && probeMessage.Length > 0)
                    ImGui.SetTooltip(probeMessage);
                break;
        }

        ImGui.Spacing();
        DrawVoicePicker();

        ImGui.Spacing();
        var volume = Config.Volume;
        ImGui.SetNextItemWidth(260f);
        if (ImGui.SliderFloat("音量", ref volume, 0f, 1f, "%.2f"))
        {
            Config.Volume = Math.Clamp(volume, 0f, 1f);
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("試播一句##test"))
            plugin.RunTest();

        ImGui.SameLine();
        ImGui.TextDisabled("（也可以用指令 /tataru test）");

        if (plugin.LastTestMessage.Length > 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextDisabled(plugin.LastTestMessage);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawVoicePicker()
    {
        var current = Config.VoiceId;

        if (probeState == SpeakerProbeState.Ok && speakers.Count > 0)
        {
            ImGui.SetNextItemWidth(300f);
            if (ImGui.BeginCombo("聲線##voice", current))
            {
                foreach (var speaker in speakers)
                {
                    var id = speaker.VoiceId.Length > 0 ? speaker.VoiceId : speaker.Name;
                    var label = speaker.Name.Length > 0 && speaker.Name != id ? $"{speaker.Name}（{id}）" : id;
                    if (ImGui.Selectable($"{label}##voice-{id}", id == current))
                    {
                        Config.VoiceId = id;
                        Config.Save();
                    }
                }

                ImGui.EndCombo();
            }

            return;
        }

        // 沒有清單就退成手填，並且在列上寫清楚「這是沒查到，不是只有這一個」。
        var voice = current;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("聲線##voiceManual", ref voice, 64))
        {
            Config.VoiceId = voice;
            Config.Save();
        }

        ImGui.TextColored(
            ColorUnknown,
            probeState == SpeakerProbeState.Failed
                ? "聲線清單：未連線，只能手動填（目前的值照樣會拿去用）"
                : "聲線清單：尚未查詢，只能手動填");
    }

    private void DrawPoolTab()
    {
        ImGui.Spacing();
        DrawPoolStats();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGeminiSettings();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawJobButtons();
    }

    /// <summary>
    /// 池統計的快取。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="PraisePool.CachedCountOf"/> 會對每一句做 <see cref="System.IO.File.Exists"/>。
    /// 直接畫在每一幀等於在 UI 執行緒上每秒做上千次磁碟查詢，所以每秒只重算一次。
    /// 副作用是預合成跑的時候數字最多晚一秒才跳——那正好是人看得懂的更新速度。
    /// </remarks>
    private readonly Dictionary<string, (int Total, int Cached)> statsCache = [];
    private DateTime statsRefreshedUtc = DateTime.MinValue;

    private void RefreshStatsIfStale()
    {
        var now = DateTime.UtcNow;
        if (statsCache.Count > 0 && now - statsRefreshedUtc < TimeSpan.FromSeconds(1)) return;
        statsRefreshedUtc = now;

        foreach (var category in PraiseCategory.All)
            statsCache[category] = (plugin.Pool.CountOf(category), plugin.Pool.CachedCountOf(category));
    }

    private void DrawPoolStats()
    {
        RefreshStatsIfStale();

        ImGui.TextDisabled("誇獎池（每個情境：句數／已合成語音的句數）");
        if (ImGui.BeginTable("##poolStats", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("情境");
            ImGui.TableSetupColumn("句數");
            ImGui.TableSetupColumn("已有語音");
            ImGui.TableHeadersRow();

            foreach (var category in PraiseCategory.All)
            {
                var (total, cached) = statsCache.TryGetValue(category, out var s) ? s : (0, 0);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(category);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(total.ToString());
                ImGui.TableNextColumn();
                if (cached == 0 && total > 0)
                    ImGui.TextColored(ColorBad, "0");
                else if (cached < total)
                    ImGui.TextColored(ColorUnknown, cached.ToString());
                else
                    ImGui.TextColored(ColorOk, cached.ToString());
            }

            ImGui.EndTable();
        }

        ImGui.TextDisabled("池與語音快取的位置");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{plugin.Pool.PoolPath}\n{plugin.Pool.CacheDirectory}");
    }

    private void DrawGeminiSettings()
    {
        ImGui.TextDisabled("Gemini（只在按「擴充誇獎池」時才會連網；遊戲中完全不用）");

        var key = Config.GeminiApiKey;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("API 金鑰##geminiKey", ref key, 256, ImGuiInputTextFlags.Password))
        {
            Config.GeminiApiKey = key;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("存在這個外掛的設定檔裡，不會進版控、不會寫進記錄檔。");

        var model = Config.GeminiModel;
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("模型##geminiModel", ref model, 128))
        {
            Config.GeminiModel = model;
            Config.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "預設 gemini-3.5-flash-lite（快、省、額度高）。\n"
                + "其他可用：gemini-flash-lite-latest、gemini-3.6-flash。\n"
                + "gemini-2.x-flash 系列對新金鑰已停用，填了會回 404。");
        }

        var count = Config.GenerateCountPerCategory;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("每個情境生幾句##genCount", ref count, 1, 50))
        {
            Config.GenerateCountPerCategory = count;
            Config.Save();
        }
    }

    private void DrawJobButtons()
    {
        var running = plugin.Jobs.IsRunning;

        ImGui.BeginDisabled(running);
        if (ImGui.Button("擴充誇獎池##expand"))
            plugin.Jobs.StartExpandPool();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("用 Gemini 針對每個情境各生一批新句子，寫進 pool.json。新句子還沒有語音。");

        ImGui.SameLine();

        ImGui.BeginDisabled(running);
        if (ImGui.Button("預合成語音快取##precache"))
            plugin.Jobs.StartPrecacheAudio();
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把池裡還沒有語音的句子逐句送去橋接合成，存成 WAV 快取。可能要跑好幾分鐘。");

        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("取消##cancelJob"))
                plugin.Jobs.Cancel();
        }

        // 進度與結果都在列上（長文字才收進 tooltip）。
        if (running)
        {
            var progress = plugin.Jobs.Progress;
            ImGui.TextColored(
                ColorUnknown,
                progress.Length > 0 ? $"{plugin.Jobs.JobName}　進行中 {progress}" : $"{plugin.Jobs.JobName}　進行中…");
        }
        else
        {
            var last = plugin.Jobs.LastResult;
            if (last.Length == 0)
            {
                ImGui.TextDisabled("上次結果：（這次啟動後還沒跑過）");
            }
            else
            {
                const int maxOnRow = 48;
                var shown = last.Length > maxOnRow ? last[..maxOnRow] + "…" : last;
                ImGui.TextUnformatted($"上次結果：{shown}");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(last);
            }
        }
    }

    private void ProbeSpeakers()
    {
        if (probeState == SpeakerProbeState.Probing) return;

        probeState = SpeakerProbeState.Probing;
        probeMessage = string.Empty;

        var host = Config.TtsHost;
        _ = Task.Run(async () =>
        {
            var result = await TtsBridge.GetSpeakersAsync(host).ConfigureAwait(false);
            if (result == null)
            {
                speakers = [];
                probeMessage = $"連不上 {host}/speakers。橋接沒開、位址不對或防火牆擋住都會這樣。詳細原因見記錄檔。";
                probeState = SpeakerProbeState.Failed;
                return;
            }

            speakers = result;
            probeState = SpeakerProbeState.Ok;
        });
    }
}
