using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetworkConditioner;

public class RelaySession : IDisposable
{
    private readonly IPEndPoint targetEndpoint;
    private readonly Socket listenSocket;
    private readonly Stopwatch clock;
    private readonly DelayedPacketScheduler scheduler;
    private readonly LinkStatistics totalUpstreamStatistics;
    private readonly LinkStatistics totalDownstreamStatistics;
    private readonly ImpairedLink upstreamLink;
    private readonly ImpairedLink downstreamLink;
    private readonly UdpClient targetClient;
    private readonly CancellationTokenSource cancellation = new();

    private long lastActivityTicks;

    public RelaySession(
        IPEndPoint clientEndpoint,
        IPEndPoint targetEndpoint,
        Socket listenSocket,
        ConditionerOptions options,
        int sessionIndex,
        Stopwatch clock,
        DelayedPacketScheduler scheduler,
        LinkStatistics totalUpstreamStatistics,
        LinkStatistics totalDownstreamStatistics)
    {
        ClientEndpoint = clientEndpoint;
        this.targetEndpoint = targetEndpoint;
        this.listenSocket = listenSocket;
        this.clock = clock;
        this.scheduler = scheduler;
        this.totalUpstreamStatistics = totalUpstreamStatistics;
        this.totalDownstreamStatistics = totalDownstreamStatistics;

        upstreamLink = new ImpairedLink(options.Upstream, new Random(options.Seed + 2 * sessionIndex));
        downstreamLink = new ImpairedLink(options.Downstream, new Random(options.Seed + 2 * sessionIndex + 1));
        targetClient = new UdpClient(0, targetEndpoint.AddressFamily);
        Touch();

        _ = Task.Run(ReceiveFromTargetLoop);
    }

    public IPEndPoint ClientEndpoint { get; }
    public LinkStatistics UpstreamStatistics { get; } = new();
    public LinkStatistics DownstreamStatistics { get; } = new();

    public TimeSpan LastActivity => TimeSpan.FromTicks(Interlocked.Read(ref lastActivityTicks));

    public void HandleClientPacket(byte[] payload)
    {
        Touch();
        var decision = upstreamLink.Process(clock.Elapsed);
        UpstreamStatistics.Record(decision, payload.Length);
        totalUpstreamStatistics.Record(decision, payload.Length);

        if (!decision.IsDropped)
            scheduler.Schedule(payload, targetClient.Client, targetEndpoint, decision.DeliveryTime);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        targetClient.Dispose();
    }

    private void Touch()
    {
        Interlocked.Exchange(ref lastActivityTicks, clock.Elapsed.Ticks);
    }

    private async Task ReceiveFromTargetLoop()
    {
        var token = cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await targetClient.ReceiveAsync(token);
                HandleTargetPacket(result.Buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
            }
            catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (SocketException e)
            {
                Console.Error.WriteLine($"Session {ClientEndpoint} receive error: {e.SocketErrorCode}");
                return;
            }
        }
    }

    private void HandleTargetPacket(byte[] payload)
    {
        Touch();
        var decision = downstreamLink.Process(clock.Elapsed);
        DownstreamStatistics.Record(decision, payload.Length);
        totalDownstreamStatistics.Record(decision, payload.Length);

        if (!decision.IsDropped)
            scheduler.Schedule(payload, listenSocket, ClientEndpoint, decision.DeliveryTime);
    }
}
