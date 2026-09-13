using System.Text.Json;
using MaggotTask.Core;

namespace MaggotTask.Services;

/// <summary>
/// JSON 持久化（%APPDATA%\MaggotTask\state.json，System.Text.Json，零外部依赖）。
/// 写入去抖：变更后标记 dirty，最长 5 秒落盘一次；退出时强制落盘。
/// </summary>
public sealed class PersistenceService : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        TypeInfoResolver = StateJsonContext.Default,
    };

    private readonly object _gate = new();
    private readonly System.Timers.Timer _timer;
    private bool _dirty;
    private AppState? _pending;
    private bool _disposed;

    public PersistenceService(TimeSpan? interval = null)
    {
        AppPaths.EnsureDataDirectory();
        _timer = new System.Timers.Timer((interval ?? TimeSpan.FromSeconds(5)).TotalMilliseconds)
        {
            AutoReset = true,
        };
        _timer.Elapsed += (_, _) => FlushIfDirty();
        _timer.Start();
    }

    /// <summary>最近一次读/写错误（null 表示正常）。</summary>
    public string? LastError { get; private set; }

    /// <summary>成功落盘的次数，便于自测观察去抖行为。</summary>
    public int SaveCount { get; private set; }

    /// <summary>读取状态；文件不存在或损坏时返回全新状态。</summary>
    public AppState Load()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile))
            {
                return new AppState { Config = new MaggotConfig() };
            }

            string json = File.ReadAllText(AppPaths.StateFile);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppState { Config = new MaggotConfig() };
            }

            AppState? state = JsonSerializer.Deserialize<AppState>(json, Options);
            if (state is null)
            {
                return new AppState { Config = new MaggotConfig() };
            }

            state.Config ??= new MaggotConfig();
            state.Config.Normalize();
            state.Tasks ??= new List<TaskItem>();
            // v1 的 state.json 里没有 dailyTemplates 键 —— 反序列化后是 null，这里补成空表。
            state.DailyTemplates ??= new List<DailyTemplate>();
            // v1 / v2 的 state.json 里没有 weeklyTemplates 键 —— 同样补成空表（不抛异常）。
            state.WeeklyTemplates ??= new List<WeeklyTemplate>();
            if (state.Version < AppState.CurrentVersion) state.Version = AppState.CurrentVersion;
            LastError = null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 读取状态失败，使用默认值: {ex.Message}");
            return new AppState { Config = new MaggotConfig() };
        }
    }

    /// <summary>标记待写内容；真正落盘由 5 秒节拍或 <see cref="Flush"/> 触发。</summary>
    public void MarkDirty(AppState state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = state;
            _dirty = true;
        }
    }

    /// <summary>立即落盘（未标记 dirty 时不做任何事）。</summary>
    public bool FlushIfDirty()
    {
        AppState? state;
        lock (_gate)
        {
            if (!_dirty || _pending is null) return false;
            state = _pending;
            _dirty = false;
        }
        return Write(state);
    }

    /// <summary>强制落盘。</summary>
    public void Flush(AppState state)
    {
        lock (_gate)
        {
            _pending = state;
            _dirty = false;
        }
        Write(state);
    }

    private bool Write(AppState state)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            state.LastSeenAt = LifecycleEngine.NowMs();
            string json = JsonSerializer.Serialize(state, Options);

            // 原子写：先写临时文件再替换，避免断电/崩溃留下半个文件。
            string tmp = AppPaths.StateFile + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(AppPaths.StateFile))
            {
                File.Replace(tmp, AppPaths.StateFile, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, AppPaths.StateFile);
            }

            SaveCount++;
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[MaggotTask] 写入状态失败: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Stop();
        _timer.Dispose();
    }
}
