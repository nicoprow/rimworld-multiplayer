namespace NetworkConditioner;

public record LinkProfile(
    double DelayMs,
    double JitterMs,
    double LossPercent,
    double BurstIntervalSeconds,
    double BurstLengthMs,
    bool AllowReorder);
