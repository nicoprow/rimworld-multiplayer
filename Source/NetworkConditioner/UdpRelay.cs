using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetworkConditioner;

public class UdpRelay : IDisposable
{
    private readonly ConditionerOptions options;
    private readonly Stopwatch clock = new();
    private readonly DelayedPacketScheduler scheduler;
    private readonly ConcurrentDictionary<IPEndPoint, RelaySession> sessions = new();
    private readonly LinkStatistics totalUpstreamStatistics = new();
    private readonly LinkStatistics totalDownstreamStatistics = new();

    private UdpClient? listenClient;
    private IPEndPoint? targetEndpoint;
    private int createdSessionCount;

    public UdpRelay(ConditionerOptions options)
    {
        this.options = options;
        scheduler = new DelayedPacketScheduler(clock);
    }

    public int SessionCount => sessions.Count;

    public async Task RunAsync(CancellationToken token)
    {
        targetEndpoint = ResolveTarget();
        listenClient = new UdpClient(new IPEndPoint(IPAddress.Any, options.ListenPort));
        clock.Start();
        scheduler.Start();

        var receiveTask = ReceiveFromClientsLoop(token);
        await MaintenanceLoop(token);
        await receiveTask;

        DisposeAllSessions();
        PrintTotals();
    }

    public void Dispose()
    {
        DisposeAllSessions();
        listenClient?.Dispose();
        scheduler.Dispose();
    }

    private IPEndPoint ResolveTarget()
    {
        var addresses = Dns.GetHostAddresses(options.TargetHost);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault()
                      ?? throw new InvalidOperationException($"Cannot resolve target host {options.TargetHost}");
        return new IPEndPoint(address, options.TargetPort);
    }

    private async Task ReceiveFromClientsLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listenClient!.ReceiveAsync(token);
                GetOrCreateSession(result.RemoteEndPoint).HandleClientPacket(result.Buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
            }
            catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
        }
    }

    private RelaySession GetOrCreateSession(IPEndPoint clientEndpoint)
    {
        if (sessions.TryGetValue(clientEndpoint, out var existing))
            return existing;

        var session = new RelaySession(
            clientEndpoint,
            targetEndpoint!,
            listenClient!.Client,
            options,
            createdSessionCount++,
            clock,
            scheduler,
            totalUpstreamStatistics,
            totalDownstreamStatistics);
        sessions[clientEndpoint] = session;
        Console.WriteLine($"Session opened: {clientEndpoint}");
        return session;
    }

    private async Task MaintenanceLoop(CancellationToken token)
    {
        var lastStatsPrint = clock.Elapsed;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RemoveIdleSessions();

            if (clock.Elapsed - lastStatsPrint >= options.StatsInterval)
            {
                lastStatsPrint = clock.Elapsed;
                PrintStatistics();
            }
        }
    }

    private void RemoveIdleSessions()
    {
        var now = clock.Elapsed;
        var idleSessions = sessions.Values.Where(s => now - s.LastActivity > options.SessionTimeout).ToList();

        foreach (var session in idleSessions)
        {
            if (!sessions.TryRemove(session.ClientEndpoint, out _))
                continue;

            session.Dispose();
            Console.WriteLine($"Session closed: {session.ClientEndpoint}");
        }
    }

    private void PrintStatistics()
    {
        foreach (var session in sessions.Values)
            Console.WriteLine($"[{session.ClientEndpoint}] up: {session.UpstreamStatistics} | down: {session.DownstreamStatistics}");

        Console.WriteLine($"[total] up: {totalUpstreamStatistics} | down: {totalDownstreamStatistics} | pending={scheduler.PendingCount}");
    }

    private void PrintTotals()
    {
        Console.WriteLine($"Final totals: up: {totalUpstreamStatistics} | down: {totalDownstreamStatistics}");
    }

    private void DisposeAllSessions()
    {
        foreach (var endpoint in sessions.Keys.ToList())
        {
            if (sessions.TryRemove(endpoint, out var session))
                session.Dispose();
        }
    }
}
