using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Monero.Common;
using Monero.Wallet.Model;

namespace Monero.Native;

/// <summary>
/// The native listener of a wallet. The callbacks run one at a time on a thread of monero-c that the listener owns, with no lock of the wallet
/// held. They only queue what they get, so the code of the program, which can be slow, never holds that thread; a task parses the output JSON
/// and calls <c>onEvent</c> on the thread pool, one event after the other.
/// </summary>
internal sealed class WalletEvents : IDisposable
{
    private readonly Channel<Event> _events = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<Action<MoneroWalletListener>> _onEvent;
    private readonly Action<Exception> _onError;
    private readonly Task _dispatcher;

    /// <summary>
    /// Whether the running code is the dispatcher of these events, or what it called: a listener, or a handler of the errors. It is set
    /// when the dispatcher starts, and flows into everything it runs, whatever thread it lands on, so a caller from there knows it can't
    /// wait for the drain: the dispatcher would wait for that caller to return, which is that code.
    /// </summary>
    private readonly AsyncLocal<bool> _onDispatcher = new();

    private GCHandle _self;

    public unsafe WalletEvents(Action<Action<MoneroWalletListener>> onEvent, Action<Exception> onError)
    {
        _onEvent = onEvent;
        _onError = onError;
        _self = GCHandle.Alloc(this);

        try
        {
            var callbacks = new WalletListenerCallbacks
            {
                StructSize = (uint)sizeof(WalletListenerCallbacks),
                UserData = (void*)GCHandle.ToIntPtr(_self),
                OnSyncProgress = &SyncProgressCallback,
                OnNewBlock = &NewBlockCallback,
                OnBalancesChanged = &BalancesChangedCallback,
                OnOutputReceived = &OutputReceivedCallback,
                OnOutputSpent = &OutputSpentCallback,
            };

            Check.Ok(NativeMethods.monero_wallet_listener_create(in callbacks, out var handle));
            Handle = handle;
        }
        catch
        {
            _self.Free();
            throw;
        }

        _dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>The native listener, to add to the wallet.</summary>
    public WalletListenerHandle Handle { get; }

    private enum Kind
    {
        SyncProgress,
        NewBlock,
        BalancesChanged,
        OutputReceived,
        OutputSpent,
    }

    private readonly record struct Event(Kind Kind, ulong A, ulong B, ulong C, double Percent, string? Text);

    private static unsafe WalletEvents Of(void* userData) => (WalletEvents)GCHandle.FromIntPtr((IntPtr)userData).Target!;

    // an exception that leaves a callback ends the process, and nothing can be done on a thread of monero-c
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void SyncProgressCallback(void* userData, ulong height, ulong startHeight, ulong endHeight, double percentDone, byte* message)
    {
        try
        {
            Of(userData)._events.Writer.TryWrite(new Event(Kind.SyncProgress, height, startHeight, endHeight, percentDone, Marshal.PtrToStringUTF8((IntPtr)message)));
        }
        catch
        {
            // see above
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void NewBlockCallback(void* userData, ulong height)
    {
        try
        {
            Of(userData)._events.Writer.TryWrite(new Event(Kind.NewBlock, height, 0, 0, 0, null));
        }
        catch
        {
            // see above
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void BalancesChangedCallback(void* userData, ulong newBalance, ulong newUnlockedBalance)
    {
        try
        {
            Of(userData)._events.Writer.TryWrite(new Event(Kind.BalancesChanged, newBalance, newUnlockedBalance, 0, 0, null));
        }
        catch
        {
            // see above
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OutputReceivedCallback(void* userData, byte* json)
    {
        try
        {
            Of(userData)._events.Writer.TryWrite(new Event(Kind.OutputReceived, 0, 0, 0, 0, Marshal.PtrToStringUTF8((IntPtr)json)));
        }
        catch
        {
            // see above
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OutputSpentCallback(void* userData, byte* json)
    {
        try
        {
            Of(userData)._events.Writer.TryWrite(new Event(Kind.OutputSpent, 0, 0, 0, 0, Marshal.PtrToStringUTF8((IntPtr)json)));
        }
        catch
        {
            // see above
        }
    }

    private async Task DispatchAsync()
    {
        // see _onDispatcher: it is set for the whole life of the dispatcher, since the code that can't wait for the drain is anything
        // this loop calls, directly or through the waits it starts
        _onDispatcher.Value = true;

        await foreach (var e in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            Action<MoneroWalletListener> call;
            try
            {
                call = ToCall(e);
            }
            catch (Exception error)
            {
                // an event that can't be read is reported, and doesn't stop the next ones
                _onError(new MoneroError("The wallet sent an event that could not be read", error));
                continue;
            }

            try
            {
                _onEvent(call);
            }
            catch (Exception error)
            {
                // a listener that throws doesn't stop the others, or the next events
                _onError(error);
            }
        }
    }

    private static Action<MoneroWalletListener> ToCall(Event e)
    {
        switch (e.Kind)
        {
            case Kind.SyncProgress:
                return listener => listener.OnSyncProgress(e.A, e.B, e.C, e.Percent, e.Text ?? "");
            case Kind.NewBlock:
                return listener => listener.OnNewBlock(e.A);
            case Kind.BalancesChanged:
                return listener => listener.OnBalancesChanged(e.A, e.B);
            case Kind.OutputReceived:
            {
                var output = MoneroJson.Deserialize<MoneroWalletBlocksResult>(e.Text!).GetOutputs().FirstOrDefault();
                return listener =>
                {
                    if (output != null) listener.OnOutputReceived(output);
                };
            }
            default:
            {
                var output = MoneroJson.Deserialize<MoneroWalletBlocksResult>(e.Text!).GetInputs().FirstOrDefault();
                return listener =>
                {
                    if (output != null) listener.OnOutputSpent(output);
                };
            }
        }
    }

    /// <summary>
    /// Wait until the events that callbacks queued so far were dispatched, then run <paramref name="after"/>: what frees the owner of the
    /// listener, which must not run while the dispatcher still dispatches. It is done after the listener was removed from the wallet.
    /// When the dispatcher itself runs the caller, from a listener or a handler of the errors, the drain can't be waited for: it would
    /// wait for the caller to return, which is that code. <paramref name="after"/> follows the exit of the dispatcher instead, and the
    /// task is done at once.
    /// </summary>
    public Task CompleteAsync(Action after)
    {
        _events.Writer.TryComplete();

        if (_onDispatcher.Value)
        {
            // see above: nothing here can wait for the dispatcher, which is running the caller
            _ = AfterTheDispatcherAsync(after);
            return Task.CompletedTask;
        }

        return AfterTheDispatcherAsync(after);
    }

    private async Task AfterTheDispatcherAsync(Action after)
    {
        await _dispatcher.ConfigureAwait(false);
        after();
    }

    /// <summary>Free the listener. It is done after the listener was removed from the wallet.</summary>
    public void Dispose()
    {
        Handle.Dispose();
        _events.Writer.TryComplete();
        if (_self.IsAllocated) _self.Free();
    }
}
