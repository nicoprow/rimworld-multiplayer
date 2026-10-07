using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NetworkConditioner;

namespace Tests;

[TestFixture]
public class NetworkConditionerTest
{
    private static readonly LinkProfile CleanProfile = new(0, 0, 0, 0, 0, false);

    private static List<LinkDecision> ProcessEveryMillisecond(LinkProfile profile, int seed, int packetCount)
    {
        var link = new ImpairedLink(profile, new Random(seed));
        var decisions = new List<LinkDecision>(packetCount);
        for (var i = 0; i < packetCount; i++)
            decisions.Add(link.Process(TimeSpan.FromMilliseconds(i)));
        return decisions;
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Test]
    public void NoLossAndNoBurstsNeverDrop()
    {
        var decisions = ProcessEveryMillisecond(CleanProfile, 1, 10000);

        decisions.Should().OnlyContain(d => !d.IsDropped);
    }

    [Test]
    public void FullLossAlwaysDrops()
    {
        var profile = CleanProfile with { LossPercent = 100 };

        var decisions = ProcessEveryMillisecond(profile, 1, 10000);

        decisions.Should().OnlyContain(d => d.DropReason == DropReason.RandomLoss);
    }

    [Test]
    public void FivePercentLossIsApproximatelyFivePercent()
    {
        var profile = CleanProfile with { LossPercent = 5 };

        var decisions = ProcessEveryMillisecond(profile, 12345, 100000);

        var lossFraction = decisions.Count(d => d.IsDropped) / 100000.0;
        lossFraction.Should().BeInRange(0.045, 0.055);
    }

    [Test]
    public void DelaysStayWithinJitterRangeWhenReorderIsOn()
    {
        var profile = CleanProfile with { DelayMs = 100, JitterMs = 30, AllowReorder = true };
        var link = new ImpairedLink(profile, new Random(7));

        for (var i = 0; i < 10000; i++)
        {
            var arrival = TimeSpan.FromMilliseconds(i);
            var delayMs = (link.Process(arrival).DeliveryTime - arrival).TotalMilliseconds;
            delayMs.Should().BeInRange(70 - 1e-6, 130 + 1e-6);
        }
    }

    [Test]
    public void DeliveryTimesNeverDecreaseWhenReorderIsOff()
    {
        var profile = CleanProfile with { DelayMs = 50, JitterMs = 500 };

        var deliveryTimes = ProcessEveryMillisecond(profile, 3, 10000).Select(d => d.DeliveryTime).ToList();

        deliveryTimes.Should().BeInAscendingOrder();
    }

    [Test]
    public void LaterPacketOvertakesEarlierOneWhenReorderIsOnWithLargeJitter()
    {
        var profile = CleanProfile with { DelayMs = 500, JitterMs = 400, AllowReorder = true };

        var deliveryTimes = ProcessEveryMillisecond(profile, 3, 1000).Select(d => d.DeliveryTime).ToList();

        var overtakeExists = deliveryTimes.Zip(deliveryTimes.Skip(1), (earlier, later) => later < earlier).Any(x => x);
        overtakeExists.Should().BeTrue();
    }

    [Test]
    public void SameSeedAndArrivalTimesProduceIdenticalDecisions()
    {
        var profile = new LinkProfile(80, 40, 10, 1, 100, false);

        var first = ProcessEveryMillisecond(profile, 99, 20000);
        var second = ProcessEveryMillisecond(profile, 99, 20000);

        first.Should().Equal(second);
    }

    [Test]
    public void BurstsDropExpectedFractionOfPackets()
    {
        var profile = CleanProfile with { BurstIntervalSeconds = 1, BurstLengthMs = 200 };
        var packetCount = 600 * 1000;

        var decisions = ProcessEveryMillisecond(profile, 2024, packetCount);

        var burstFraction = decisions.Count(d => d.DropReason == DropReason.Burst) / (double)packetCount;
        burstFraction.Should().BeInRange(0.12, 0.21);
    }

    [Test]
    public void SharedOptionsApplyToBothDirections()
    {
        var options = ConditionerOptions.Parse(
            ["--listen", "9000", "--target", "example.com:8000", "--delay", "50.5", "--jitter", "10", "--loss", "2", "--reorder", "--seed", "5"]);

        options.ListenPort.Should().Be(9000);
        options.TargetHost.Should().Be("example.com");
        options.TargetPort.Should().Be(8000);
        options.Seed.Should().Be(5);
        options.Upstream.Should().Be(new LinkProfile(50.5, 10, 2, 0, 0, true));
        options.Downstream.Should().Be(options.Upstream);
    }

    [Test]
    public void DirectionOverridesOnlyChangeOneDirectionRegardlessOfOrder()
    {
        var options = ConditionerOptions.Parse(
            ["--up-delay", "300", "--listen", "9000", "--target", "localhost:8000", "--delay", "100", "--down-loss", "7"]);

        options.Upstream.DelayMs.Should().Be(300);
        options.Upstream.LossPercent.Should().Be(0);
        options.Downstream.DelayMs.Should().Be(100);
        options.Downstream.LossPercent.Should().Be(7);
    }

    [TestCase("--target", "localhost:8000")]
    [TestCase("--listen", "9000")]
    public void MissingRequiredOptionThrows(string optionName, string optionValue)
    {
        var parse = () => ConditionerOptions.Parse([optionName, optionValue]);

        parse.Should().Throw<ArgumentException>();
    }

    [TestCase("--bogus", "1")]
    [TestCase("--delay", "-5")]
    [TestCase("--delay", "abc")]
    [TestCase("--loss", "101")]
    [TestCase("--up-loss", "150")]
    [TestCase("--burst-interval", "2")]
    [TestCase("--listen", "0")]
    [TestCase("--stats-interval", "0")]
    public void InvalidInputThrows(string optionName, string optionValue)
    {
        string[] args = ["--listen", "9000", "--target", "localhost:8000", optionName, optionValue];

        var parse = () => ConditionerOptions.Parse(args);

        parse.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task RelayForwardsDatagramToEchoServerAndBack()
    {
        using var echoServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echoServer.Client.LocalEndPoint!).Port;
        using var stopEcho = new CancellationTokenSource();
        var echoTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var request = await echoServer.ReceiveAsync(stopEcho.Token);
                    await echoServer.SendAsync(request.Buffer, request.RemoteEndPoint, stopEcho.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
            }
        });

        var relayPort = GetFreeUdpPort();
        var options = new ConditionerOptions(
            relayPort, "127.0.0.1", echoPort, CleanProfile, CleanProfile, 1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        using var relay = new UdpRelay(options);
        using var stopRelay = new CancellationTokenSource();
        var relayTask = relay.RunAsync(stopRelay.Token);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        byte[] message = [1, 2, 3, 4, 5];
        await client.SendAsync(message, new IPEndPoint(IPAddress.Loopback, relayPort), timeout.Token);
        var reply = await client.ReceiveAsync(timeout.Token);

        reply.Buffer.Should().Equal(message);

        stopRelay.Cancel();
        await relayTask;
        stopEcho.Cancel();
        await echoTask;
    }
}
