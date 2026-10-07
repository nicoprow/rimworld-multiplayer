using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetworkConditioner;

public class DelayedPacketScheduler : IDisposable
{
    private readonly record struct PendingPacket(byte[] Payload, Socket Sender, IPEndPoint Destination);

    private readonly Stopwatch clock;
    private readonly object queueLock = new();
    private readonly PriorityQueue<PendingPacket, (TimeSpan DeliveryTime, long Sequence)> queue = new();
    private readonly Thread deliveryThread;

    private long nextSequence;
    private long sendErrors;
    private volatile bool running;
    private bool timerResolutionRaised;

    public DelayedPacketScheduler(Stopwatch clock)
    {
        this.clock = clock;
        deliveryThread = new Thread(DeliveryLoop)
        {
            Name = "NetworkConditioner delivery",
            IsBackground = true
        };
    }

    public int PendingCount
    {
        get
        {
            lock (queueLock)
                return queue.Count;
        }
    }

    public long SendErrors => Interlocked.Read(ref sendErrors);

    public void Start()
    {
        if (running)
            return;

        running = true;

        if (OperatingSystem.IsWindows())
        {
            timeBeginPeriod(1);
            timerResolutionRaised = true;
        }

        deliveryThread.Start();
    }

    public void Schedule(byte[] payload, Socket sender, IPEndPoint destination, TimeSpan deliveryTime)
    {
        lock (queueLock)
        {
            var sequence = nextSequence++;
            queue.Enqueue(new PendingPacket(payload, sender, destination), (deliveryTime, sequence));
        }
    }

    public void Dispose()
    {
        if (!running)
            return;

        running = false;
        deliveryThread.Join();

        if (timerResolutionRaised)
        {
            timeEndPeriod(1);
            timerResolutionRaised = false;
        }
    }

    private void DeliveryLoop()
    {
        while (running)
        {
            DeliverDuePackets();
            Thread.Sleep(1);
        }
    }

    private void DeliverDuePackets()
    {
        while (TryDequeueDuePacket(out var packet))
            Send(packet);
    }

    private bool TryDequeueDuePacket(out PendingPacket packet)
    {
        lock (queueLock)
        {
            var hasDuePacket = queue.TryPeek(out _, out var priority) && priority.DeliveryTime <= clock.Elapsed;
            if (hasDuePacket)
            {
                packet = queue.Dequeue();
                return true;
            }
        }

        packet = default;
        return false;
    }

    private void Send(PendingPacket packet)
    {
        try
        {
            packet.Sender.SendTo(packet.Payload, packet.Destination);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Interlocked.Increment(ref sendErrors);
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);
}
