namespace NetworkConditioner;

public class ImpairedLink
{
    private readonly LinkProfile profile;
    private readonly Random random;

    private bool firstBurstScheduled;
    private TimeSpan burstStart;
    private TimeSpan burstEnd;
    private TimeSpan lastDeliveryTime;

    public ImpairedLink(LinkProfile profile, Random random)
    {
        this.profile = profile;
        this.random = random;
    }

    public LinkDecision Process(TimeSpan arrivalTime)
    {
        var insideBurst = UpdateBurstWindow(arrivalTime);
        var lossSample = random.NextDouble();
        var jitterSample = random.NextDouble();

        if (insideBurst)
            return new LinkDecision(DropReason.Burst, TimeSpan.Zero);

        if (lossSample < profile.LossPercent / 100.0)
            return new LinkDecision(DropReason.RandomLoss, TimeSpan.Zero);

        var jitterMs = (jitterSample * 2.0 - 1.0) * profile.JitterMs;
        var delayMs = Math.Max(0.0, profile.DelayMs + jitterMs);
        var deliveryTime = arrivalTime + TimeSpan.FromMilliseconds(delayMs);

        if (!profile.AllowReorder && deliveryTime < lastDeliveryTime)
            deliveryTime = lastDeliveryTime;

        lastDeliveryTime = deliveryTime;
        return new LinkDecision(DropReason.None, deliveryTime);
    }

    private bool UpdateBurstWindow(TimeSpan arrivalTime)
    {
        if (profile.BurstIntervalSeconds <= 0)
            return false;

        if (!firstBurstScheduled)
        {
            firstBurstScheduled = true;
            ScheduleBurstAfter(TimeSpan.Zero);
        }

        while (arrivalTime >= burstEnd)
            ScheduleBurstAfter(burstEnd);

        return arrivalTime >= burstStart;
    }

    private void ScheduleBurstAfter(TimeSpan previousEnd)
    {
        var gapMs = SampleExponential(profile.BurstIntervalSeconds * 1000.0);
        var lengthMs = SampleExponential(profile.BurstLengthMs);
        burstStart = previousEnd + TimeSpan.FromMilliseconds(gapMs);
        burstEnd = burstStart + TimeSpan.FromMilliseconds(lengthMs);
    }

    private double SampleExponential(double mean)
    {
        return -mean * Math.Log(1.0 - random.NextDouble());
    }
}
