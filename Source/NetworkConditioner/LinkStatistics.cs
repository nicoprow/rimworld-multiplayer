namespace NetworkConditioner;

public class LinkStatistics
{
    private long received;
    private long forwarded;
    private long droppedByRandomLoss;
    private long droppedByBurst;
    private long bytesForwarded;

    public long Received => Interlocked.Read(ref received);
    public long Forwarded => Interlocked.Read(ref forwarded);
    public long DroppedByRandomLoss => Interlocked.Read(ref droppedByRandomLoss);
    public long DroppedByBurst => Interlocked.Read(ref droppedByBurst);
    public long BytesForwarded => Interlocked.Read(ref bytesForwarded);

    public void Record(LinkDecision decision, int byteCount)
    {
        Interlocked.Increment(ref received);

        switch (decision.DropReason)
        {
            case DropReason.RandomLoss:
                Interlocked.Increment(ref droppedByRandomLoss);
                break;
            case DropReason.Burst:
                Interlocked.Increment(ref droppedByBurst);
                break;
            default:
                Interlocked.Increment(ref forwarded);
                Interlocked.Add(ref bytesForwarded, byteCount);
                break;
        }
    }

    public override string ToString()
    {
        return $"rx={Received} fwd={Forwarded} lost={DroppedByRandomLoss} burst={DroppedByBurst} bytes={BytesForwarded}";
    }
}
