using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Monero.Common;
using Monero.Daemon.Model;
using Monero.Native;
using Monero.Wallet.Model;

namespace Monero.UnitTests;

/// <summary>
/// Unit tests of the events that the native listeners of a wallet and a daemon dispatch. They need the library of monero-c, which the
/// build has for the platform it ran on, and no wallet, daemon or network: the tests queue what the callbacks of monero-c would, and read
/// what the dispatch does with it.
/// </summary>
[TestFixture]
public class MoneroNativeEventsUnitTests
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
    }

    [Test]
    public async Task TheDaemonDispatchesTheHeadersThatTheCallbacksQueue()
    {
        var headers = new List<MoneroBlockHeader>();
        var events = new DaemonEvents(headers.Add, _ => { });

        QueueHeader(events, """{"height":1}""");
        QueueHeader(events, """{"height":2}""");
        await events.CompleteAsync(events.Dispose);

        Assert.That(headers.Select(header => header.Height), Is.EqualTo(new ulong[] { 1, 2 }));
    }

    [Test]
    public async Task AHeaderOfTheDaemonThatCannotBeReadIsReportedAndSkipped()
    {
        var headers = new List<MoneroBlockHeader>();
        var errors = new List<Exception>();
        var events = new DaemonEvents(headers.Add, errors.Add);

        QueueHeader(events, "not a header");
        QueueHeader(events, """{"height":3}""");
        await events.CompleteAsync(events.Dispose);

        Assert.That(headers.Select(header => header.Height), Is.EqualTo(new ulong[] { 3 }));
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Is.InstanceOf<MoneroError>());
        Assert.That(errors[0]!.Message, Is.EqualTo("The daemon sent a block header that could not be read"));
        Assert.That(errors[0]!.InnerException, Is.InstanceOf<JsonException>());
    }

    [Test]
    public async Task AListenerOfTheDaemonThatThrowsIsReportedAndSkipped()
    {
        var errors = new List<Exception>();
        var events = new DaemonEvents(_ => throw new InvalidOperationException("the listener threw"), errors.Add);

        QueueHeader(events, """{"height":4}""");
        QueueHeader(events, """{"height":5}""");
        await events.CompleteAsync(events.Dispose);

        Assert.That(errors.Select(error => error.Message), Is.EqualTo(new[] { "the listener threw", "the listener threw" }));
    }

    [Test]
    public async Task TheWalletDispatchesTheEventsThatTheCallbacksQueue()
    {
        var heights = new List<ulong>();
        var events = new WalletEvents(call => call(new RecordingListener(heights)), _ => { });

        QueueEvent(events, "NewBlock", 5, null);
        QueueEvent(events, "NewBlock", 6, null);
        await events.CompleteAsync(events.Dispose);

        Assert.That(heights, Is.EqualTo(new ulong[] { 5, 6 }));
    }

    [Test]
    public async Task AnEventOfTheWalletThatCannotBeReadIsReportedAndSkipped()
    {
        var heights = new List<ulong>();
        var errors = new List<Exception>();
        var events = new WalletEvents(call => call(new RecordingListener(heights)), errors.Add);

        QueueEvent(events, "OutputReceived", 0, "not an output");
        QueueEvent(events, "NewBlock", 7, null);
        await events.CompleteAsync(events.Dispose);

        Assert.That(heights, Is.EqualTo(new ulong[] { 7 }));
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Is.InstanceOf<MoneroError>());
        Assert.That(errors[0]!.Message, Is.EqualTo("The wallet sent an event that could not be read"));
        Assert.That(errors[0]!.InnerException, Is.InstanceOf<JsonException>());
    }

    [Test]
    public async Task AListenerOfTheWalletThatThrowsIsReportedAndSkipped()
    {
        var errors = new List<Exception>();
        var events = new WalletEvents(_ => throw new InvalidOperationException("the listener threw"), errors.Add);

        QueueEvent(events, "NewBlock", 8, null);
        QueueEvent(events, "NewBlock", 9, null);
        await events.CompleteAsync(events.Dispose);

        Assert.That(errors.Select(error => error.Message), Is.EqualTo(new[] { "the listener threw", "the listener threw" }));
    }

    // writes the channel of the daemon the way its callback in monero-c does
    private static void QueueHeader(DaemonEvents events, string json)
    {
        var channel = (Channel<string>)typeof(DaemonEvents).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;
        Assert.That(channel.Writer.TryWrite(json), Is.True);
    }

    // writes the channel of the wallet the way its callbacks in monero-c do, with an event of the given kind
    private static void QueueEvent(WalletEvents events, string kind, ulong a, string? text)
    {
        var type = typeof(WalletEvents).GetNestedType("Event", BindingFlags.NonPublic)!;
        var value = Activator.CreateInstance(type, Enum.Parse(typeof(WalletEvents).GetNestedType("Kind", BindingFlags.NonPublic)!, kind), a, 0UL, 0UL, 0.0, text)!;
        var channel = typeof(WalletEvents).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;
        var writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
        Assert.That((bool)writer.GetType().GetMethod("TryWrite", new[] { type })!.Invoke(writer, new[] { value })!, Is.True);
    }

    private sealed class RecordingListener(List<ulong> heights) : MoneroWalletListener
    {
        public override void OnNewBlock(ulong height)
        {
            heights.Add(height);
        }
    }
}
