using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TataruPraise.Core;

/// <summary>
/// WAV 播放。照抄 Saucy 的 <c>Core/Plugin/Saucy.Audio.cs</c> 寫法。
/// </summary>
/// <remarks>
/// 🔴 <b>整個「解碼 + Init + Play」都在 <see cref="Task.Run(Action)"/> 上跑，絕不在 framework／主執行緒。</b>
/// 讀檔、解碼、開音訊裝置都是阻塞呼叫；2026-07-25 艦隊有兩個 repo 就是因為在 tick 上做這件事被修過。
/// NAudio 的 <see cref="WaveOutEvent"/> 不像 Dalamud 的 D3D11 材質 API 綁在算繪執行緒上，整包丟到背景是安全的。
/// <para>
/// 🔴 NAudio 底層走 WinMM，有 AccessViolation 面（.NET Core 的 corrupted-state exception，
/// <c>try/catch</c> 完全攔不到）。<b>所以這裡刻意不自創播放路徑</b>：元件、順序、Dispose 時機
/// 全部跟 Saucy 一樣，只把「從檔案讀」換成「從 <see cref="MemoryStream"/> 讀」並多包一層音量。
/// </para>
/// <para>
/// 📌 <b>同時只播一句，後來的直接丟棄（不排隊）。</b> 誇獎是氣氛用的，排隊會變成連珠炮；
/// 而且丟棄讓「有沒有出聲」只取決於當下狀態，不會累積出使用者預期不到的延遲爆發。
/// 丟棄時 <c>TryPlay</c> 回 <c>false</c>，呼叫端（含 IPC）看得出來。
/// </para>
/// </remarks>
public sealed class AudioPlayer : IDisposable
{
    private readonly object gate = new();
    private WaveOutEvent? waveOut;
    private WaveFileReader? reader;
    private bool busy;
    private bool disposed;

    /// <summary>現在正在播嗎。</summary>
    public bool IsBusy
    {
        get { lock (gate) return busy; }
    }

    /// <summary>
    /// 丟一段 WAV 去播。已經在播（或已 Dispose）就直接回 <c>false</c>，不排隊。
    /// </summary>
    /// <param name="wavBytes">完整的 WAV 位元組（含檔頭）。</param>
    /// <param name="volume">0～1。</param>
    public bool TryPlay(byte[] wavBytes, float volume)
    {
        if (wavBytes.Length == 0) return false;

        lock (gate)
        {
            if (disposed || busy) return false;
            busy = true;
        }

        var clamped = Math.Clamp(volume, 0f, 1f);
        Task.Run(() => PlayCore(wavBytes, clamped));
        return true;
    }

    /// <summary>從磁碟上的 WAV 檔播。讀檔本身也在背景做。</summary>
    public bool TryPlayFile(string path, float volume)
    {
        lock (gate)
        {
            if (disposed || busy) return false;
            busy = true;
        }

        var clamped = Math.Clamp(volume, 0f, 1f);
        Task.Run(() =>
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                Svc.Log.Information($"[TataruPraise] 讀取語音快取失敗：{ex.Message}");
                lock (gate) busy = false;
                return;
            }

            PlayCore(bytes, clamped);
        });

        return true;
    }

    private void PlayCore(byte[] wavBytes, float volume)
    {
        try
        {
            lock (gate)
            {
                if (disposed)
                {
                    busy = false;
                    return;
                }

                DisposeAudioLocked();

                // MemoryStream 是 WaveFileReader 的來源；WaveFileReader(Stream) 不會關掉它，
                // 而純記憶體的 stream 也不需要被關，所以不必額外持有參考。
                reader = new WaveFileReader(new MemoryStream(wavBytes, writable: false));
                var sample = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume };

                waveOut = new WaveOutEvent();
                waveOut.PlaybackStopped += OnPlaybackStopped;
                waveOut.Init(sample);
                waveOut.Play();
            }
        }
        catch (Exception ex)
        {
            // 沒有音訊裝置、WAV 檔頭壞掉之類的都走這裡：只是不出聲，不影響遊戲。
            Svc.Log.Information($"[TataruPraise] 播放失敗：{ex.Message}");
            lock (gate)
            {
                DisposeAudioLocked();
                busy = false;
            }
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (gate)
        {
            DisposeAudioLocked();
            busy = false;
        }
    }

    /// <summary>必須在持有 <see cref="gate"/> 時呼叫。</summary>
    private void DisposeAudioLocked()
    {
        if (waveOut != null)
        {
            waveOut.PlaybackStopped -= OnPlaybackStopped;
            waveOut.Dispose();
            waveOut = null;
        }

        reader?.Dispose();
        reader = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            DisposeAudioLocked();
            busy = false;
        }
    }
}
