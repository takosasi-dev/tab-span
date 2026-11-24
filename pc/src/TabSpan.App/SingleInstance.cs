// 多重起動の防止(FR-24): ユーザーごとの名前付き Mutex を持てた方だけが動く。2 つ目は名前付きのイベントで先の方へ「前に出して」と合図して終わる。
// 名前は Local\TabSpan-<ユーザーの SID>(確かめでは別の名前を渡す)。合図を受けたら Activated を(スレッドプールから)呼ぶ。
using System.Security.Principal;

namespace TabSpan.App;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle signal, Action activated)
    {
        _mutex = mutex;
        _signal = signal;
        _wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => activated(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static string DefaultName => $@"Local\TabSpan-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    /// <summary>先に動いている物が無ければ持ち主になって返す。あれば合図を送って null。</summary>
    public static SingleInstance? TryAcquire(string name, Action activated)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        if (!createdNew)
        {
            signal.Set();
            signal.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, signal, activated);
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
