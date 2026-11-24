// 仮想ディスプレイの番(FR-10・FR-19・D-18・§10): 映す前に外れていれば付け、切断から時間が来たら外して位置を state.json に覚える。
// state.json には「TabSpan が付けた/映していた物」も付けた時点で書き、強制終了の後の起動で片付ける。
// 実在のモニター(isVirtual が偽)には Detach も Attach も呼ばない(INV-9)。呼ぼうとしたら InvalidOperationException。
using System.Text.Json;
using TabSpan.Core;

namespace TabSpan.Platform;

public sealed class VirtualDisplayKeeper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate, // 名前の大小を区別しない表に読み込む
    };

    private readonly IDisplayControl _display;
    private readonly Func<string, bool> _isVirtual;
    private readonly string _statePath;
    private readonly Func<bool> _detachOnDisconnect;
    private readonly Action<string> _log;
    private readonly DetachPolicy _policy;
    private readonly Lock _lock = new();
    private readonly State _state;

    public VirtualDisplayKeeper(IDisplayControl display, Func<string, bool> isVirtual, string statePath, IClock clock, Func<bool> detachOnDisconnect, Func<int> delaySec,
        Action<string> log)
    {
        _display = display;
        _isVirtual = isVirtual;
        _statePath = statePath;
        _detachOnDisconnect = detachOnDisconnect;
        _log = log;
        _policy = new DetachPolicy(clock, delaySec);
        _state = Load(statePath, log);
    }

    /// <summary>外した(通知用。引数は deviceName)。</summary>
    public event Action<string>? Detached;

    /// <summary>FR-10: 外れている仮想ディスプレイを、覚えた位置(無ければ主モニターの右隣)で付ける。実在のモニターなら何もせず true。失敗は false。</summary>
    public bool EnsureAttached(string deviceName)
    {
        if (!_isVirtual(deviceName))
        {
            return true; // FR-11: 実在のモニターはそのまま
        }

        lock (_lock)
        {
            if (_display.CurrentPlacement(deviceName) is not null)
            {
                if (_state.Owned.TryAdd(deviceName, false))
                {
                    Save();
                }

                return true;
            }

            _state.Owned[deviceName] = true;
            Save(); // 付ける前に書く(強制終了しても次の起動で片付く)
            var placement = _state.Detached.GetValueOrDefault(deviceName) ?? DisplayPlacement.Unknown;
            if (!AttachVirtual(deviceName, placement))
            {
                _state.Owned.Remove(deviceName);
                Save();
                _log($"仮想ディスプレイ {deviceName} を付けられなかった(もう一度は試さない)");
                return false;
            }

            _state.Detached.Remove(deviceName);
            Save();
            _log($"仮想ディスプレイ {deviceName} を付けた");
            return true;
        }
    }

    public void OnSessionStarted() => _policy.OnConnected();

    public void OnSessionEnded() => _policy.OnDisconnected();

    /// <summary>D-18: 切断から delaySec が過ぎたら(detachOnDisconnect が真のとき)、TabSpan が付けた/映していた仮想ディスプレイを外す。</summary>
    public void Tick()
    {
        if (_policy.ShouldDetachNow() && _detachOnDisconnect())
        {
            DetachOwned(everything: true);
        }
    }

    /// <summary>FR-19: PC 側の終了のとき。TabSpan が付けた物は外す。映していただけの物は detachOnDisconnect が真のときだけ外す。</summary>
    public void DetachOwnedNow() => DetachOwned(everything: _detachOnDisconnect());

    /// <summary>§10: 起動時に、前回の state.json に残った物(強制終了で付いたまま)を DetachOwnedNow と同じ決まりで片付ける。</summary>
    public void CleanupFromState()
    {
        lock (_lock)
        {
            if (_state.Owned.Count > 0)
            {
                _log($"前回の終わりに付いたままだった仮想ディスプレイを片付ける({_state.Owned.Count} 枚)");
            }
        }

        DetachOwnedNow();
    }

    /// <summary>INV-9 の関門: 仮想と判定した物だけを外す。</summary>
    internal bool DetachVirtual(string deviceName)
    {
        ThrowIfNotVirtual(deviceName);
        return _display.Detach(deviceName);
    }

    /// <summary>INV-9 の関門: 仮想と判定した物だけを付ける。</summary>
    internal bool AttachVirtual(string deviceName, DisplayPlacement placement)
    {
        ThrowIfNotVirtual(deviceName);
        return _display.Attach(deviceName, placement);
    }

    private void ThrowIfNotVirtual(string deviceName)
    {
        if (!_isVirtual(deviceName))
        {
            throw new InvalidOperationException($"{deviceName} は仮想ディスプレイと判定していないので、付け外ししない(INV-9)");
        }
    }

    private void DetachOwned(bool everything)
    {
        var detached = new List<string>();
        lock (_lock)
        {
            foreach (var (name, attachedByUs) in _state.Owned.ToList())
            {
                _state.Owned.Remove(name);
                if (!everything && !attachedByUs)
                {
                    continue;
                }

                if (!_isVirtual(name))
                {
                    _log($"{name} は仮想ディスプレイと判定しなくなったので外さない");
                    continue;
                }

                if (_display.CurrentPlacement(name) is not { } placement)
                {
                    continue; // もう外れている
                }

                _state.Detached[name] = placement;
                Save(); // 外す前に位置を書く
                if (DetachVirtual(name))
                {
                    detached.Add(name);
                    _log($"仮想ディスプレイ {name} を外した(位置 {placement.X},{placement.Y} 大きさ {placement.Width}x{placement.Height})");
                }
                else
                {
                    _state.Detached.Remove(name);
                    _log($"仮想ディスプレイ {name} を外せなかった(もう一度は試さない)");
                }
            }

            Save();
        }

        foreach (var name in detached)
        {
            Detached?.Invoke(name);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_statePath))!);
            var temp = _statePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_state, JsonOptions));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"state.json を書けない: {ex.Message}");
        }
    }

    private static State Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            return new State();
        }

        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(path), JsonOptions);
            if (state is not null)
            {
                return state;
            }
        }
        catch (JsonException)
        {
        }

        log("state.json を読めないので、覚えた位置なしで始める");
        return new State();
    }

    /// <summary>state.json の中身。Owned の値は「TabSpan が付けた」(偽なら映していただけ)。Detached は外す前の位置。</summary>
    internal sealed class State
    {
        public Dictionary<string, bool> Owned { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DisplayPlacement> Detached { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
