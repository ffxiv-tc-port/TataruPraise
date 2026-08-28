# 塔塔露誇獎（TataruPraise）

台服（TC）Dalamud 外掛。偶爾依遊戲狀態，用塔塔露的克隆聲線念一句繁體中文的誇獎。
**純本機、內容健全（SFW）、預設全部關閉。**

指令：

| 指令 | 作用 |
|---|---|
| `/tataru` | 開啟設定視窗 |
| `/tataru test` | 試播一句（不受總開關限制） |

---

## 它怎麼運作

```
遊戲事件（Dalamud）─→ 冷卻 + 機率 ─→ 從誇獎池挑一句 ─→ 播事先合成好的 WAV
```

**執行期完全不連網。** 句子與語音都事先準備好放在本機，遊戲中只是挑一句播檔案。
連網只發生在設定視窗裡那兩個按鈕（擴充誇獎池、預合成語音快取），以及 IPC `Speak` 遇到沒快取的句子時。

資料放在外掛的設定資料夾底下（`%AppData%\...\Dalamud\Config\pluginConfigs\TataruPraise\`）：

- `pool.json` —— 依情境分類的誇獎句，格式 `{"情境": [{"text": "…", "wav": "cache/….wav"}]}`
- `cache/<sha1>.wav` —— 語音快取，檔名是句子的 SHA-1

第一次載入時如果池是空的，會灌入 28 句內建的預設誇獎（副本完成 8、升等 7、登入 7、Gil 里程碑 6），
所以**沒有 Gemini 金鑰的人也可以直接按「預合成語音快取」就開始用**。
內建句只在整個池一句都沒有的時候才灌，不會覆蓋你自己的池。

## 觸發

四個觸發**全部預設關閉**，另外還有一個總開關。命中後要先過全域冷卻（預設 120 秒），再過觸發機率（預設 30%）。

| 觸發 | 來源 | 備註 |
|---|---|---|
| 副本完成 | `IDutyState.DutyCompleted` | |
| 升等 | `IClientState.LevelChanged` | 只有等級**真的往上跳**才算；登入時的等級回報、切職業的等級回報都不觸發 |
| 登入 | `IClientState.Login` | |
| Gil 里程碑 | 每 5 秒讀一次身上的 Gil | 跨過設定的整數倍（預設 100 萬）才觸發；登入後第一次讀到的數字只當基準 |

> 🔴 **刻意不做**任何靠聊天訊息文字比對的觸發（成就、製作大成功之類）。
> 台服的中文字面沒辦法離線確定，照國際服寫死一定錯，而且錯法是靜默的。

## 語音橋接（前提）

語音來自本機的 **GPT-SoVITS 橋接**（`gsv_bridge`，預設 `http://127.0.0.1:9882`），
背後是跨語言克隆（日配參考音訊 → 中文輸出）。橋接與遊戲同機就用 `127.0.0.1`；
跑在另一台機器的話，那邊要把橋接與 api_v2 綁 `0.0.0.0`、防火牆放行 9882，設定裡填區網 IP。

外掛只用到兩個端點：

```
GET  {host}/speakers
  → [{"name":"塔塔露","voice_id":"塔塔露"}, …]

POST {host}/
  {"text":"要念的中文", "text_lang":"zh", "ref_audio_path":"./参考音频/塔塔露.wav"}
  → 200，body 就是 WAV bytes
```

只送這三個欄位就好：橋接會依 `ref_audio_path` 的檔名對到聲線，自動補參考音訊、逐字稿、
`prompt_lang` 以及全部穩定化參數。錯誤語意：`404` = 聲線沒設定、`502` = 橋接背後的 api_v2 連不上。

> 🔴 送去合成的句子**必須含自然的中文標點**（逗號、頓號、句號、驚嘆號）。
> 沒有標點的長句會讓聲線越念越高變成怪腔——這是實測結論，不是風格偏好。
> 所以擴充池時會把沒有標點的生成結果直接丟掉。

**橋接連不上的時候外掛只是不出聲，不會崩、不會卡遊戲。**

## 擴充誇獎池（Gemini，選用）

設定視窗 →「誇獎池」分頁 → 填 Gemini API 金鑰 → 按「擴充誇獎池」。
會對四個情境各要一批新句子（預設每個情境 10 句），要求模型輸出 JSON 陣列，去重後寫進 `pool.json`。
接著按「預合成語音快取」把新句子逐句送去橋接合成。

- 預設模型 `gemini-3.5-flash-lite`，可自填。其他可用：`gemini-flash-lite-latest`、`gemini-3.6-flash`。
  **`gemini-2.x-flash` 系列對新金鑰已停用**（回 404），別填。
- `429`（額度）／`503`（過載）會指數退避重試最多 3 次；其他錯誤直接放棄。失敗只寫 `Information` 級記錄，不出聲、不彈窗。
- **金鑰存在這個外掛的設定檔裡**（Dalamud `GetPluginConfig`），不進版控、不會寫進記錄檔。

> 設定裡的「文字後端」列舉保留了「雲端即時」與「本機即時（Ollama）」兩個值，
> 但**第一版沒有實作**，選了等同純池模式。

---

## IPC 契約

其他外掛可以直接呼叫。契約名逐字定義在 `TataruPraise/IpcContract.cs`。

> 🔴 **這三個名字不會改。** Dalamud 的 CallGate 是純字串比對，改名不會有錯誤訊息，
> 呼叫端只會拿到「沒有人註冊」——靜默斷線。要換語意會開新名字，舊的留著。

| 契約名 | 簽章 | 行為 |
|---|---|---|
| `TataruPraise.Speak` | `Func<string, bool>` | 念這一句。先查語音快取，沒有就背景送 9882 即時合成（逾時 10 秒）並順便存進快取。**不吃冷卻、不吃機率**，但吃總開關與「同時只播一句」。回傳是「有沒有排進去」，不代表真的出得了聲。 |
| `TataruPraise.Praise` | `Func<string, bool>` | 從指定情境的誇獎池挑一句已合成的來播。**不看事件開關、不看機率，但吃冷卻**與總開關。情境字串＝`副本完成` / `升等` / `登入` / `Gil里程碑`。 |
| `TataruPraise.IsAvailable` | `Func<bool>` | 總開關開著**而且**池裡真的有已合成語音的句子。 |

呼叫範例：

```csharp
var speak = pluginInterface.GetIpcSubscriber<string, bool>("TataruPraise.Speak");
try
{
    speak.InvokeFunc("前輩，這一手漂亮！");
}
catch (Exception)
{
    // 對方沒安裝／沒載入時 InvokeFunc 會擲 IpcNotReadyError，呼叫端自己要接。
}
```

## 建置

```bash
dotnet build TataruPraise/TataruPraise.csproj -c Release -p:DalamudLibPath=D:/ffxiv-tc-port/Dalamud/bin/Release/
```

（尾端斜線不能省。）Dalamud 釘在 API13（`dalamud-pin-v13.0.0.16`），目標 `net9.0-windows7.0`。
音訊走 NAudio 2.3.0（`WaveOutEvent` + `WaveFileReader` + `VolumeSampleProvider`），
**整個解碼與播放都在背景執行緒**，不碰 framework tick。

## 授權

與艦隊其他外掛一致。語音模型、參考音訊與橋接本身不屬於本 repo。
