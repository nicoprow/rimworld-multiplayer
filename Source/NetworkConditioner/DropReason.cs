namespace NetworkConditioner;

public enum DropReason
{
    None,
    RandomLoss,
    Burst
}

public readonly record struct LinkDecision(DropReason DropReason, TimeSpan DeliveryTime)
{
    public bool IsDropped => DropReason != DropReason.None;
}
