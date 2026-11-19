// ==============================================================================
// 文件: ResultAudioPlayer.cs
// 命名空间: BlueRandom.Modules.RandomProvider
// 作用: 演播结果窗音频播放器，管理抽卡音效 (SFX) 与背景音乐 (BGM) 的加载、
//       播放、循环、音量淡入淡出推进与资源释放。
// ==============================================================================

using System;
using System.IO;
using System.Windows.Media;

namespace BlueRandom.Modules.RandomProvider;

/// <summary>
/// 演播结果窗音频播放器，负责抽卡音效与 BGM 的播放控制及音量淡入淡出计算。
/// </summary>
internal sealed class ResultAudioPlayer : IDisposable
{
    private MediaPlayer? _sfxPlayer;
    private MediaPlayer? _bgmPlayer;
    private double _bgmTargetVolume;
    private double _bgmCurrentVolume;
    private double _bgmFadeSec = 1.5;
    private bool _isBgmFadingIn;
    private bool _isBgmFadingOut;

    /// <summary>
    /// 获取当前是否正在执行 BGM 淡入。
    /// </summary>
    public bool IsBgmFadingIn => _isBgmFadingIn;

    /// <summary>
    /// 获取或设置当前是否正在执行 BGM 淡出。
    /// </summary>
    public bool IsBgmFadingOut
    {
        get => _isBgmFadingOut;
        set => _isBgmFadingOut = value;
    }

    /// <summary>
    /// 获取当前是否处于 BGM 淡入或淡出过程中。
    /// </summary>
    public bool IsFading => _isBgmFadingIn || _isBgmFadingOut;

    /// <summary>
    /// 开始 BGM 淡出流程。
    /// </summary>
    public void FadeOut() => _isBgmFadingOut = true;

    /// <summary>
    /// 播放抽卡音效（SFX）。
    /// </summary>
    /// <param name="props">弹窗配置参数。</param>
    public void PlaySfx(PickResultDialogConfig props)
    {
        if (!props.DefaultPlayGachaSound) return;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(dir);
            string? path = null;

            if (!string.IsNullOrEmpty(props.GachaSoundCustomId))
            {
                var custom = CustomsStore.LoadCustom(props.GachaSoundCustomId);
                if (custom != null && !string.IsNullOrEmpty(custom.Base64))
                {
                    byte[] bytes = Convert.FromBase64String(custom.Base64);
                    string ext = Path.GetExtension(custom.FileName);
                    if (string.IsNullOrEmpty(ext)) ext = ".wav";
                    path = Path.Combine(dir, $"{custom.Id}{ext}");
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                byte[]? bytes = ResourceCache.GetAudioBytes("button_click.wav");
                if (bytes != null)
                {
                    path = Path.Combine(dir, "button_click.wav");
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                }
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            _sfxPlayer ??= new MediaPlayer();
            _sfxPlayer.Stop();
            _sfxPlayer.Close();
            _sfxPlayer.Volume = Math.Clamp(props.SoundVolume / 100.0, 0.0, 1.0);
            _sfxPlayer.Open(new Uri(path));
            _sfxPlayer.Play();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"播放抽卡音效失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 播放背景音乐（BGM），并启动淡入流程。
    /// </summary>
    /// <param name="props">弹窗配置参数。</param>
    public void PlayBgm(PickResultDialogConfig props)
    {
        StopBgmImmediate();
        if (!props.PlayMusic || string.IsNullOrEmpty(props.BgmCustomId)) return;

        try
        {
            var custom = CustomsStore.LoadCustom(props.BgmCustomId);
            if (custom == null || string.IsNullOrEmpty(custom.Base64)) return;

            string dir = Path.Combine(Path.GetTempPath(), "BlueRandom_Audio");
            Directory.CreateDirectory(dir);
            byte[] bytes = Convert.FromBase64String(custom.Base64);
            string ext = Path.GetExtension(custom.FileName);
            if (string.IsNullOrEmpty(ext)) ext = ".mp3";
            string path = Path.Combine(dir, $"{custom.Id}{ext}");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                File.WriteAllBytes(path, bytes);
            }

            _bgmPlayer = new MediaPlayer();
            _bgmTargetVolume = Math.Clamp(props.MusicVolume / 100.0, 0.0, 1.0);
            _bgmFadeSec = Math.Max(0.1, props.BgmFadeDuration);
            _bgmCurrentVolume = 0.0;
            _isBgmFadingIn = true;
            _isBgmFadingOut = false;

            _bgmPlayer.MediaEnded -= OnBgmEnded;
            _bgmPlayer.MediaEnded += OnBgmEnded;
            _bgmPlayer.Volume = 0.0;
            _bgmPlayer.Open(new Uri(path));
            _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
            _bgmPlayer.Play();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DCompFRW", $"播放 BGM 失败: {ex.Message}");
        }
    }

    private void OnBgmEnded(object? sender, EventArgs e)
    {
        try
        {
            if (_bgmPlayer != null && !_isBgmFadingOut)
            {
                var props = ConfigCache.GetFrwProps();
                _bgmPlayer.Position = TimeSpan.FromSeconds(Math.Max(0.0, props.BgmStartTime));
                _bgmPlayer.Play();
            }
        }
        catch { }
    }

    /// <summary>
    /// 每帧推进 BGM 淡入淡出音量渐变（由渲染 tick 调用）。
    /// </summary>
    public void Update()
    {
        if (_bgmPlayer == null) return;

        double step = 16.0 / Math.Max(1.0, _bgmFadeSec * 1000.0);
        if (_isBgmFadingIn && !_isBgmFadingOut)
        {
            _bgmCurrentVolume = Math.Min(_bgmTargetVolume, _bgmCurrentVolume + _bgmTargetVolume * step);
            _bgmPlayer.Volume = _bgmCurrentVolume;
            if (_bgmCurrentVolume >= _bgmTargetVolume - 0.001) _isBgmFadingIn = false;
        }
        else if (_isBgmFadingOut)
        {
            _bgmCurrentVolume = Math.Max(0.0, _bgmCurrentVolume - Math.Max(0.0001, _bgmCurrentVolume * step));
            _bgmPlayer.Volume = _bgmCurrentVolume;
            if (_bgmCurrentVolume <= 0.001) StopBgmImmediate();
        }
    }

    /// <summary>
    /// 立即停止并释放当前播放中的 BGM。
    /// </summary>
    public void StopBgmImmediate()
    {
        if (_bgmPlayer == null) return;
        try
        {
            _bgmPlayer.Volume = 0.0;
            _bgmPlayer.Stop();
            _bgmPlayer.Close();
        }
        catch { }
        _bgmPlayer = null;
        _bgmCurrentVolume = 0.0;
        _isBgmFadingIn = false;
        _isBgmFadingOut = false;
    }

    /// <summary>
    /// 释放播放器占用的所有音频资源（停止并关闭 SFX 与 BGM）。
    /// </summary>
    public void Dispose()
    {
        StopBgmImmediate();
        try
        {
            if (_sfxPlayer != null)
            {
                _sfxPlayer.Stop();
                _sfxPlayer.Close();
                _sfxPlayer = null;
            }
        }
        catch { }
    }
}
