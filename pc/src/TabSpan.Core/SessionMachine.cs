// PC 側のセッションの状態(Hello 待ち → ペアリング中 / 配信 → 終了)と、状態ごとに端末から受けてよい type。
// enum に無い type は false を返す。「知らない type は読み飛ばしてログ」の扱いは、呼ぶ側が Enum.IsDefined で先に分ける。
namespace TabSpan.Core;

public enum SessionState { AwaitingHello, Pairing, Streaming, Closed }

public sealed class SessionMachine
{
    private readonly Lock _lock = new();
    private SessionState _state = SessionState.AwaitingHello;

    public SessionState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>今の状態で、端末から受けてよい type か。PC から端末へ送る type はどの状態でも false。</summary>
    public bool Accepts(MessageType t) => State switch
    {
        SessionState.AwaitingHello => t is MessageType.Hello or MessageType.PairRequest or MessageType.Bye,
        SessionState.Pairing => t is MessageType.Bye,
        // INV-4: Touch・RequestKeyframe などは認証の済んだ配信の間だけ
        SessionState.Streaming => t is MessageType.RequestKeyframe or MessageType.Stats or MessageType.Touch or MessageType.Pong or MessageType.Bye,
        _ => false,
    };

    /// <summary>PairRequest を受け付けた(INV-6 の確かめは呼ぶ側)。AwaitingHello からだけ。</summary>
    public void BeginPairing() => Move(SessionState.AwaitingHello, SessionState.Pairing);

    /// <summary>Hello のトークンが合い、HelloAck(ok=true) を送る。AwaitingHello からだけ。</summary>
    public void Authenticate() => Move(SessionState.AwaitingHello, SessionState.Streaming);

    /// <summary>どの状態からでも終了へ。何度呼んでもよい。</summary>
    public void Close()
    {
        lock (_lock)
        {
            _state = SessionState.Closed;
        }
    }

    private void Move(SessionState from, SessionState to)
    {
        lock (_lock)
        {
            if (_state != from)
            {
                throw new InvalidOperationException($"{_state} から {to} へは移れない");
            }

            _state = to;
        }
    }
}
