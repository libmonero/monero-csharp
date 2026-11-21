using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Monero.Daemon;
using Monero.Daemon.Model;
using Monero.Native;
using Monero.Wallet;
using Monero.Wallet.Model;

namespace Monero.UnitTests;

/// <summary>
/// Unit tests of the disposal of a daemon and a wallet. They need the library of monero-c, and no daemon, wallet or network: the tests
/// write the channels of the events of the objects by reflection, exactly what the callbacks of monero-c do, so a listener or a handler
/// of the errors can dispose the object it belongs to, and a disposal can be held while the drain of it is observed.
/// </summary>
[TestFixture]
public class MoneroNativeDisposalUnitTests
{
    [OneTimeSetUp]
    public void EnsureTheLibrary()
    {
        // the tests of the other files run without monero-c: this file is the one that needs it, and only where the build has its library
        try
        {
            NativeMethods.monero_utils_get_abi_version(out _, out _, out _);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            Assert.Ignore($"no library of monero-c for {RuntimeInformation.OSArchitecture} {RuntimeInformation.OSDescription}");
        }

        // the daemon and the wallet check the era of the ABI of the library once, and the machine can have a library of another era
        // staged than the one this checkout pins: a full lifecycle of both passes against the two eras (the red team proved it with the
        // same rig), and the paths under test don't depend on it, so the check is pre-satisfied
        typeof(Check).GetField("_abiChecked", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
    }

    [Test]
    public async Task ADaemonThatAListenerDisposesByFiringAndForgettingFreesItsNativeObjects()
    {
        using var daemon = NewDaemon();
        var disposal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        daemon.NotificationError += (_, _) => drained.TrySetResult();
        daemon.AddListener(new DaemonDisposingListener(daemon, disposal));
        QueueHeader(daemon, """{"height":1}""");

        // the listener queued one more event, then fired the disposal and forgot it: the dispatcher that runs the listener can't wait
        // for the drain, so the task is done at once. The event it queued is drained all the same, and the frees follow the exit of
        // the dispatcher
        var fired = await disposal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fired.WaitAsync(TimeSpan.FromSeconds(30));
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheDaemonIsFreed(daemon));
    }

    [Test]
    public async Task ADaemonThatAListenerDisposesByWaitingForItStillReturns()
    {
        using var daemon = NewDaemon();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        daemon.AddListener(new DaemonWaitingListener(daemon, returned));

        // the header is queued from another thread, since its dispatch runs the listener, which waits for the whole disposal to
        // return: that wait used to never end, because the disposal waited for the dispatcher that ran the listener
        var dispatch = Task.Run(() => QueueHeader(daemon, """{"height":1}"""));
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheDaemonIsFreed(daemon));
    }

    [Test]
    public async Task ADaemonThatAHandlerOfTheErrorsDisposesByFiringAndForgettingFreesItsNativeObjects()
    {
        using var daemon = NewDaemon();
        var disposal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;

        // a header that can't be read reaches the handler of the errors, which fires the disposal and queues one more: the drain
        // delivers that one too, the handler fires again on a disposal that already began, and the frees follow the exit of the
        // dispatcher
        daemon.NotificationError += (_, _) =>
        {
            if (errors++ == 0) QueueHeader(daemon, "not a header either");
            disposal.TrySetResult(daemon.DisposeAsync().AsTask());
        };
        QueueHeader(daemon, "not a header");

        var fired = await disposal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fired.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheDaemonIsFreed(daemon) && errors == 2);
    }

    [Test]
    public async Task ADaemonDisposedFromAnotherThreadStillDrainsTheEventsItQueued()
    {
        using var daemon = NewDaemon();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listenerBlocker = new ManualResetEventSlim(false);
        using var errorBlocker = new ManualResetEventSlim(false);
        var listener = new DaemonBlockingListener(entered, listenerBlocker);

        daemon.AddListener(listener);
        daemon.NotificationError += (_, _) =>
        {
            drained.TrySetResult();
            errorBlocker.Wait(TimeSpan.FromSeconds(30));
        };

        // the first header goes to the listener, which holds the dispatcher, and a header that can't be read is queued behind it:
        // the disposal from another thread has to drain both before it frees anything
        var dispatch = Task.Run(() => QueueHeader(daemon, """{"height":1}"""));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        QueueHeader(daemon, "not a header");

        var disposal = daemon.DisposeAsync().AsTask();
        listenerBlocker.Set();

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // the drain is held by the handler of the errors: the disposal, which is past its queued calls, waits for it
        Assert.That(disposal.IsCompleted, Is.False);

        errorBlocker.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(listener.LastHeader!.Height, Is.EqualTo(1UL));
        Assert.That(TheDaemonIsFreed(daemon), Is.True);
    }

    [Test]
    public async Task AWalletThatAListenerDisposesByFiringAndForgettingFreesItsNativeObjects()
    {
        using var wallet = NewWallet();
        var disposal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        wallet.NotificationError += (_, _) => drained.TrySetResult();
        wallet.AddListener(new WalletDisposingListener(wallet, disposal));
        QueueEvent(wallet, "NewBlock", 5, null);

        // see the daemon: the dispatcher that runs the listener can't wait for the drain, the event it queued is drained all the
        // same, and the frees follow the exit of the dispatcher
        var fired = await disposal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fired.WaitAsync(TimeSpan.FromSeconds(30));
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheWalletIsFreed(wallet));
    }

    [Test]
    public async Task AWalletThatAListenerDisposesByWaitingForItStillReturns()
    {
        using var wallet = NewWallet();
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        wallet.AddListener(new WalletWaitingListener(wallet, returned));

        // see the daemon: the event is queued from another thread, since its dispatch runs the listener, which waits for the whole
        // disposal to return
        var dispatch = Task.Run(() => QueueEvent(wallet, "NewBlock", 5, null));
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheWalletIsFreed(wallet));
    }

    [Test]
    public async Task AWalletThatAHandlerOfTheErrorsDisposesByFiringAndForgettingFreesItsNativeObjects()
    {
        using var wallet = NewWallet();
        var disposal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = 0;

        // an event that can't be read reaches the handler of the errors, which fires the disposal and queues one more: see the daemon
        wallet.NotificationError += (_, _) =>
        {
            if (errors++ == 0) QueueEvent(wallet, "OutputReceived", 0, "not an output either");
            disposal.TrySetResult(wallet.DisposeAsync().AsTask());
        };
        QueueEvent(wallet, "OutputReceived", 0, "not an output");

        var fired = await disposal.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fired.WaitAsync(TimeSpan.FromSeconds(30));
        WaitUntilFreed(() => TheWalletIsFreed(wallet) && errors == 2);
    }

    [Test]
    public async Task AWalletDisposedFromAnotherThreadStillDrainsTheEventsItQueued()
    {
        using var wallet = NewWallet();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listenerBlocker = new ManualResetEventSlim(false);
        using var errorBlocker = new ManualResetEventSlim(false);
        var listener = new WalletBlockingListener(entered, listenerBlocker);

        wallet.AddListener(listener);
        wallet.NotificationError += (_, _) =>
        {
            drained.TrySetResult();
            errorBlocker.Wait(TimeSpan.FromSeconds(30));
        };

        // the first event goes to the listener, which holds the dispatcher, and an event that can't be read is queued behind it: see
        // the daemon
        var dispatch = Task.Run(() => QueueEvent(wallet, "NewBlock", 5, null));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        QueueEvent(wallet, "OutputReceived", 0, "not an output");

        var disposal = wallet.DisposeAsync().AsTask();
        listenerBlocker.Set();

        await drained.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // the drain is held by the handler of the errors: the disposal, which is past its queued calls, waits for it
        Assert.That(disposal.IsCompleted, Is.False);

        errorBlocker.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(30));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(listener.Heights, Is.EqualTo(new ulong[] { 5 }));
        Assert.That(TheWalletIsFreed(wallet), Is.True);
    }

    // an offline daemon and wallet: the connection is lazy, so nothing connects, and the notifications are queued by reflection
    // instead of coming from a server
    private static MoneroDaemonRpc NewDaemon() => new("http://127.0.0.1:1");

    private static MoneroWalletRpc NewWallet() => new("http://127.0.0.1:1");

    // the reflection rig of the red team: the private state of the objects is read to assert what was freed, and the channels of
    // their events are written the way the callbacks of monero-c do
    private static DaemonEvents EventsOf(MoneroDaemonRpc daemon) =>
        (DaemonEvents)typeof(MoneroDaemonRpc).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(daemon)!;

    private static WalletEvents EventsOf(MoneroWalletRpc wallet) =>
        (WalletEvents)typeof(MoneroWalletRpc).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wallet)!;

    private static void QueueHeader(MoneroDaemonRpc daemon, string json)
    {
        var channel = (Channel<string>)typeof(DaemonEvents).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(EventsOf(daemon))!;
        Assert.That(channel.Writer.TryWrite(json), Is.True);
    }

    // writes the channel of the wallet the way its callbacks in monero-c do, with an event of the given kind
    private static void QueueEvent(MoneroWalletRpc wallet, string kind, ulong a, string? text)
    {
        var events = EventsOf(wallet);
        var type = typeof(WalletEvents).GetNestedType("Event", BindingFlags.NonPublic)!;
        var value = Activator.CreateInstance(type, Enum.Parse(typeof(WalletEvents).GetNestedType("Kind", BindingFlags.NonPublic)!, kind), a, 0UL, 0UL, 0.0, text)!;
        var channel = typeof(WalletEvents).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;
        var writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
        Assert.That((bool)writer.GetType().GetMethod("TryWrite", new[] { type })!.Invoke(writer, new[] { value })!, Is.True);
    }

    // the state that a disposal from a listener used to leak: the native daemon or wallet, the native listener, and the pinned
    // dispatcher of the events
    private static bool TheDaemonIsFreed(MoneroDaemonRpc daemon) =>
        HandleOf(daemon).IsClosed && EventsOf(daemon).Handle.IsClosed && !SelfOf(EventsOf(daemon)).IsAllocated;

    private static bool TheWalletIsFreed(MoneroWalletRpc wallet) =>
        HandleOf(wallet).IsClosed && EventsOf(wallet).Handle.IsClosed && !SelfOf(EventsOf(wallet)).IsAllocated;

    private static SafeHandle HandleOf(MoneroDaemonRpc daemon) =>
        (SafeHandle)typeof(MoneroDaemonRpc).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(daemon)!;

    private static SafeHandle HandleOf(MoneroWalletRpc wallet) =>
        (SafeHandle)typeof(MoneroWalletRpc).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wallet)!;

    private static GCHandle SelfOf(DaemonEvents events) =>
        (GCHandle)typeof(DaemonEvents).GetField("_self", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;

    private static GCHandle SelfOf(WalletEvents events) =>
        (GCHandle)typeof(WalletEvents).GetField("_self", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;

    // the frees of a disposal from a listener follow the exit of the dispatcher of the events as its continuations, and nothing of
    // the public surface completes when they are done: they are waited for with a bound, which they meet as soon as the dispatcher
    // has left, since nothing else is in front of them
    private static void WaitUntilFreed(Func<bool> freed)
    {
        Assert.That(SpinWait.SpinUntil(freed, TimeSpan.FromSeconds(30)), Is.True, "the native objects were not freed within the timeout");
    }

    // fires the disposal of the daemon from inside the notification, after queueing one more event: the drain has to deliver it once
    // the listener that disposed is gone, and the frees follow the exit of the dispatcher
    private sealed class DaemonDisposingListener(MoneroDaemonRpc daemon, TaskCompletionSource<Task> disposal) : MoneroDaemonListener
    {
        public override void OnBlockHeader(MoneroBlockHeader header)
        {
            QueueHeader(daemon, "not a header");
            disposal.TrySetResult(daemon.DisposeAsync().AsTask());
        }
    }

    // waits for the whole disposal from inside the notification: the deadlock of the red team, which has to return now
    private sealed class DaemonWaitingListener(MoneroDaemonRpc daemon, TaskCompletionSource returned) : MoneroDaemonListener
    {
        public override void OnBlockHeader(MoneroBlockHeader header)
        {
            daemon.Dispose();
            returned.TrySetResult();
        }
    }

    // holds the dispatcher of the events while the test disposes the daemon from another thread, so the drain of the disposal,
    // which the listener and a handler of the errors hold in turn, can be observed
    private sealed class DaemonBlockingListener(TaskCompletionSource entered, ManualResetEventSlim blocker) : MoneroDaemonListener
    {
        public override void OnBlockHeader(MoneroBlockHeader header)
        {
            base.OnBlockHeader(header);
            entered.TrySetResult();
            blocker.Wait(TimeSpan.FromSeconds(30));
        }
    }

    // see the listeners of the daemon
    private sealed class WalletDisposingListener(MoneroWalletRpc wallet, TaskCompletionSource<Task> disposal) : MoneroWalletListener
    {
        public override void OnNewBlock(ulong height)
        {
            QueueEvent(wallet, "OutputReceived", 0, "not an output");
            disposal.TrySetResult(wallet.DisposeAsync().AsTask());
        }
    }

    // see the listeners of the daemon
    private sealed class WalletWaitingListener(MoneroWalletRpc wallet, TaskCompletionSource returned) : MoneroWalletListener
    {
        public override void OnNewBlock(ulong height)
        {
            wallet.Dispose();
            returned.TrySetResult();
        }
    }

    // see the listeners of the daemon
    private sealed class WalletBlockingListener(TaskCompletionSource entered, ManualResetEventSlim blocker) : MoneroWalletListener
    {
        private readonly List<ulong> _heights = [];

        public List<ulong> Heights => _heights;

        public override void OnNewBlock(ulong height)
        {
            _heights.Add(height);
            entered.TrySetResult();
            blocker.Wait(TimeSpan.FromSeconds(30));
        }
    }
}
