// タッチの注入のスレッド(§9.8): InitializeTouchInjection と InjectTouchInput は、このスレッド 1 本からだけ呼ぶ。
// 端末からの枠・有効と無効の切り替え・セッションの始めと終わりは Post で列に積み、10 ms ごとに TouchRelay.Tick を回す。
// フラグの組み合わせは Microsoft Learn「InjectTouchInput」の表のとおり(押す・更新・離す。取り消しは離す + CANCELED)。
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TabSpan.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TabSpan.Platform;

/// <summary>本物の注入。作れるのは注入のスレッドの中だけ(Create)。</summary>
public sealed class Win32TouchInjector : ITouchInjector
{
    private const POINTER_FLAGS Down = POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT | POINTER_FLAGS.POINTER_FLAG_DOWN;
    private const POINTER_FLAGS Update = POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT | POINTER_FLAGS.POINTER_FLAG_UPDATE;
    private const POINTER_FLAGS Up = POINTER_FLAGS.POINTER_FLAG_UP;
    private const POINTER_FLAGS Cancel = POINTER_FLAGS.POINTER_FLAG_UP | POINTER_FLAGS.POINTER_FLAG_CANCELED;

    private Win32TouchInjector()
    {
    }

    /// <summary>InitializeTouchInjection(10, TOUCH_FEEDBACK_DEFAULT)。失敗なら null とエラーの番号。</summary>
    public static Win32TouchInjector? Create(out int error)
    {
        error = 0;
        if (PInvoke.InitializeTouchInjection(ContactTable.MaxContacts, TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_DEFAULT))
        {
            return new Win32TouchInjector();
        }

        error = Marshal.GetLastPInvokeError();
        return null;
    }

    public bool Inject(IReadOnlyList<InjectedContact> contacts, out int error)
    {
        var infos = new POINTER_TOUCH_INFO[contacts.Count];
        for (var i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            infos[i] = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                    pointerId = (uint)c.Slot,
                    pointerFlags = c.Change switch
                    {
                        ContactChange.Down => Down,
                        ContactChange.Up => Up,
                        ContactChange.Cancel => Cancel,
                        _ => Update,
                    },
                    ptPixelLocation = new System.Drawing.Point(c.X, c.Y),
                },
                touchMask = PInvoke.TOUCH_MASK_CONTACTAREA | PInvoke.TOUCH_MASK_ORIENTATION | PInvoke.TOUCH_MASK_PRESSURE,
                rcContact = new RECT { left = c.X - 2, top = c.Y - 2, right = c.X + 2, bottom = c.Y + 2 },
                orientation = 90,
                pressure = 512,
            };
        }

        error = 0;
        if (PInvoke.InjectTouchInput(infos))
        {
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }
}

/// <summary>注入しない注入器(serve の確かめ用など)。受けた物を on に渡すだけ。</summary>
public sealed class CallbackTouchInjector(Action<IReadOnlyList<InjectedContact>> on) : ITouchInjector
{
    public bool Inject(IReadOnlyList<InjectedContact> contacts, out int error)
    {
        on(contacts);
        error = 0;
        return true;
    }
}

public sealed class TouchThread : IDisposable
{
    private readonly BlockingCollection<Action<TouchRelay>> _queue = [];
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _enabled;
    private volatile bool _available = true;

    /// <param name="createInjector">注入のスレッドの中で 1 回呼ぶ。null なら注入できない(Available が偽)。</param>
    public TouchThread(Func<ITouchInjector?> createInjector, bool enabled, int refreshMs, IClock clock, Action<string> log)
    {
        _enabled = enabled;
        _thread = new Thread(() => Run(createInjector, refreshMs, clock, log)) { IsBackground = true, Name = "TabSpan touch" };
        _thread.Start();
        _ready.Task.Wait();
    }

    /// <summary>注入の準備に失敗した(§9.8: タッチの機能を無効にし、画面にその旨を出す)。</summary>
    public bool Available => _available;

    /// <summary>今タッチを注入するか(HelloAck と Notice の touchEnabled)。</summary>
    public bool Enabled => _enabled && _available;

    /// <summary>注入の失敗が続いて自分で切った。注入のスレッドから来る。</summary>
    public event Action? AutoDisabled;

    public LatencySummary TakeLatency()
    {
        var done = new TaskCompletionSource<LatencySummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPost(r => done.TrySetResult(r.Latency.TakeSecond())))
        {
            return new LatencySummary(0, 0, 0);
        }

        return done.Task.Wait(1000) ? done.Task.Result : new LatencySummary(0, 0, 0);
    }

    public void Begin(ScreenRect? rect) => TryPost(r => r.Begin(rect));

    public void SetRect(ScreenRect? rect) => TryPost(r => r.SetRect(rect));

    public void End() => TryPost(r => r.End());

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        TryPost(r => r.SetEnabled(enabled));
    }

    /// <summary>端末からの枠。Touch なら注入し、どの枠でも 2 秒の見張りを延ばす。</summary>
    public void OnFrame(Frame frame)
    {
        if (frame.Type == (byte)MessageType.Touch)
        {
            var received = SystemClock.Instance.NowUs;
            TryPost(r => r.OnTouch(frame.Payload.Span, received));
        }
        else
        {
            TryPost(r => r.OnFrameSeen());
        }
    }

    private bool TryPost(Action<TouchRelay> action)
    {
        try
        {
            return _queue.TryAdd(action);
        }
        catch (InvalidOperationException)
        {
            return false; // 終わった後
        }
    }

    private void Run(Func<ITouchInjector?> createInjector, int refreshMs, IClock clock, Action<string> log)
    {
        var injector = createInjector();
        _available = injector is not null;
        var relay = new TouchRelay(injector ?? new CallbackTouchInjector(_ => { }), clock, log) { RefreshMs = refreshMs };
        relay.SetEnabled(Enabled);
        relay.AutoDisabled += () =>
        {
            _enabled = false;
            AutoDisabled?.Invoke();
        };
        _ready.TrySetResult();
        while (!_queue.IsCompleted)
        {
            try
            {
                if (_queue.TryTake(out var action, 10))
                {
                    action(relay);
                }

                relay.Tick();
            }
            catch (InvalidOperationException)
            {
                break; // CompleteAdding の後
            }
        }

        relay.End(); // 押したままの接点を残さない(INV-7)
    }

    public void Dispose()
    {
        TryPost(r => r.End());
        _queue.CompleteAdding();
        _thread.Join(2000);
        _queue.Dispose();
    }
}
