using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Monero.Common;
using Monero.Daemon.Model;

namespace Monero.Native;

/// <summary>
/// The native listener of a daemon. Callbacks only queue the header JSON, since they run on a thread of monero-cpp; a task parses it and calls
/// <c>onHeader</c> on the thread pool.
/// </summary>
internal sealed class DaemonEvents : IDisposable
{
    private readonly Channel<string> _events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<MoneroBlockHeader> _onHeader;
    private readonly Action<Exception> _onError;
    private readonly Task _dispatcher;

    /// <summary>
    /// Whether the running code is the dispatcher of these events, or what it called: a listener, or a handler of the errors. It is set
    /// when the dispatcher starts, and flows into everything it runs, whatever thread it lands on, so a caller from there knows it can't
    /// wait for the drain: the dispatcher would wait for that caller to return, which is that code.
    /// </summary>
    private readonly AsyncLocal<bool> _onDispatcher = new();

    private GCHandle _self;

    public unsafe DaemonEvents(Action<MoneroBlockHeader> onHeader, Action<Exception> onError)
    {
        _onHeader = onHeader;
        _onError = onError;
        _self = GCHandle.Alloc(this);

        try
        {
            var callbacks = new DaemonListenerCallbacks
            {
                StructSize = (uint)sizeof(DaemonListenerCallbacks),
                UserData = (void*)GCHandle.ToIntPtr(_self),
                OnBlockHeader = &BlockHeaderCallback,
            };

            Check.Ok(NativeMethods.monero_daemon_listener_create(in callbacks, out var handle));
            Handle = handle;
        }
        catch
        {
            _self.Free();
            throw;
        }

        _dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>The native listener, to add to the daemon.</summary>
    public DaemonListenerHandle Handle { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void BlockHeaderCallback(void* userData, byte* json)
    {
        // an exception that leaves a callback ends the process
        try
        {
            var events = (DaemonEvents)GCHandle.FromIntPtr((IntPtr)userData).Target!;
            events._events.Writer.TryWrite(Marshal.PtrToStringUTF8((IntPtr)json)!);
        }
        catch
        {
            // nothing can be done on a thread of monero-cpp
        }
    }

    private async Task DispatchAsync()
    {
        // see _onDispatcher: it is set for the whole life of the dispatcher, since the code that can't wait for the drain is anything
        // this loop calls, directly or through the waits it starts
        _onDispatcher.Value = true;

        await foreach (var json in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            MoneroBlockHeader header;
            try
            {
                header = MoneroJson.Deserialize<MoneroBlockHeader>(json);
            }
            catch (Exception error)
            {
                // a header that can't be read is reported, and doesn't stop the next ones
                _onError(new MoneroError("The daemon sent a block header that could not be read", error));
                continue;
            }

            try
            {
                _onHeader(header);
            }
            catch (Exception error)
            {
                // a listener that throws doesn't stop the others, or the next headers
                _onError(error);
            }
        }
    }

    /// <summary>
    /// Wait until the events that callbacks queued so far were dispatched, then run <paramref name="after"/>: what frees the owner of the
    /// listener, which must not run while the dispatcher still dispatches. It is done after the listener was removed from the daemon.
    /// When the dispatcher itself runs the caller, from a listener or a handler of the errors, the drain can't be waited for: it would
    /// wait for the caller to return, which is that code. <paramref name="after"/> follows the exit of the dispatcher instead, and the
    /// task is done at once.
    /// </summary>
    public Task CompleteAsync(Action after)
    {
        _events.Writer.TryComplete();

        if (_onDispatcher.Value)
        {
            // see above: nothing here can wait for the dispatcher, which is running the caller.
            // the task is discarded without an observer: a fault of the dispatcher here would skip the frees silently, which cannot
            // happen while every failure the dispatcher runs is reported through the guarded error handlers
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

    /// <summary>Free the listener. It is done after the listener was removed from the daemon.</summary>
    public void Dispose()
    {
        Handle.Dispose();
        _events.Writer.TryComplete();
        if (_self.IsAllocated) _self.Free();
    }
}
