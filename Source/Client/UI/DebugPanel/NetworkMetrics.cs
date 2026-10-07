using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Multiplayer.Common;

namespace Multiplayer.Client.DebugUi;

internal static class NetworkMetrics
{
    public const int RecentSampleCount = 600;

    private const double UnmatchedCommandExpiryMs = 30_000;
    private const int PendingExecutionPruneThreshold = 256;
    private const double PendingExecutionExpiryMs = 120_000;

    private static readonly Stopwatch clock = Stopwatch.StartNew();

    private static readonly LinkedList<SentCommand> sentCommandsAwaitingServer = new();
    private static readonly Dictionary<ScheduledCommand, double> ownCommandsAwaitingExecution = new();

    private static bool isStalled;
    private static double stallStartedAtMs;

    public static readonly CircularBuffer<int> RecentBufferDepths = new(RecentSampleCount);
    public static readonly CircularBuffer<int> RecentTicksPerFrame = new(RecentSampleCount);
    public static readonly CircularBuffer<float> RecentStallDurationsMs = new(RecentSampleCount);
    public static readonly CircularBuffer<float> RecentCommandRoundTripsMs = new(RecentSampleCount);
    public static readonly CircularBuffer<float> RecentCommandLatenciesMs = new(RecentSampleCount);

    public static int StallCount { get; private set; }
    public static double TotalStallTimeMs { get; private set; }
    public static int LastFrameTicksRun { get; private set; }
    public static bool IsStalled => isStalled;
    public static double CurrentStallDurationMs => isStalled ? NowMs - stallStartedAtMs : 0;

    public static event Action<float> StallEnded;
    public static event Action<float> CommandRoundTripMeasured;
    public static event Action<float> CommandLatencyMeasured;

    private static double NowMs => clock.Elapsed.TotalMilliseconds;

    public static void RecordTickFrame(int bufferDepthAtFrameStart, int ticksRun, bool waitedForServer)
    {
        LastFrameTicksRun = ticksRun;
        RecentBufferDepths.Add(bufferDepthAtFrameStart);
        RecentTicksPerFrame.Add(ticksRun);

        if (waitedForServer)
            BeginStallIfNotStalled();
        else
            EndStallIfStalled();
    }

    public static void InterruptStall()
    {
        isStalled = false;
        LastFrameTicksRun = 0;
    }

    public static void RecordCommandSent(CommandType type, int mapId, byte[] data)
    {
        double now = NowMs;
        RemoveSentCommandsOlderThan(now - UnmatchedCommandExpiryMs);
        sentCommandsAwaitingServer.AddLast(new SentCommand(new OwnCommandSignature(type, mapId, data), now));
    }

    public static void RecordCommandReceived(ScheduledCommand cmd)
    {
        if (!cmd.IsIssuedBySelf()) return;

        double? sentAtMs = TakeMatchingSentCommandTime(cmd);
        if (sentAtMs is not double sentAt) return;

        double now = NowMs;
        float roundTripMs = (float)(now - sentAt);
        RecentCommandRoundTripsMs.Add(roundTripMs);
        CommandRoundTripMeasured?.Invoke(roundTripMs);

        PruneExpiredCommandsAwaitingExecution(now);
        ownCommandsAwaitingExecution[cmd] = sentAt;
    }

    public static void RecordCommandExecuted(ScheduledCommand cmd)
    {
        if (!ownCommandsAwaitingExecution.TryGetValue(cmd, out double sentAt)) return;
        ownCommandsAwaitingExecution.Remove(cmd);

        float latencyMs = (float)(NowMs - sentAt);
        RecentCommandLatenciesMs.Add(latencyMs);
        CommandLatencyMeasured?.Invoke(latencyMs);
    }

    public static void Reset()
    {
        sentCommandsAwaitingServer.Clear();
        ownCommandsAwaitingExecution.Clear();
        isStalled = false;
        StallCount = 0;
        TotalStallTimeMs = 0;
        LastFrameTicksRun = 0;
        RecentBufferDepths.Clear();
        RecentTicksPerFrame.Clear();
        RecentStallDurationsMs.Clear();
        RecentCommandRoundTripsMs.Clear();
        RecentCommandLatenciesMs.Clear();
    }

    private static void BeginStallIfNotStalled()
    {
        if (isStalled) return;
        isStalled = true;
        stallStartedAtMs = NowMs;
    }

    private static void EndStallIfStalled()
    {
        if (!isStalled) return;
        isStalled = false;

        float stallDurationMs = (float)(NowMs - stallStartedAtMs);
        StallCount++;
        TotalStallTimeMs += stallDurationMs;
        RecentStallDurationsMs.Add(stallDurationMs);
        StallEnded?.Invoke(stallDurationMs);
    }

    private static double? TakeMatchingSentCommandTime(ScheduledCommand cmd)
    {
        var candidate = sentCommandsAwaitingServer.First;
        while (candidate != null)
        {
            if (candidate.Value.signature.Matches(cmd))
            {
                double sentAtMs = candidate.Value.sentAtMs;
                RemoveAllUpTo(candidate);
                return sentAtMs;
            }

            candidate = candidate.Next;
        }

        return null;
    }

    private static void RemoveAllUpTo(LinkedListNode<SentCommand> lastNodeToRemove)
    {
        while (sentCommandsAwaitingServer.First != lastNodeToRemove)
            sentCommandsAwaitingServer.RemoveFirst();
        sentCommandsAwaitingServer.RemoveFirst();
    }

    private static void RemoveSentCommandsOlderThan(double cutoffMs)
    {
        while (sentCommandsAwaitingServer.First is { } oldest && oldest.Value.sentAtMs < cutoffMs)
            sentCommandsAwaitingServer.RemoveFirst();
    }

    private static void PruneExpiredCommandsAwaitingExecution(double now)
    {
        if (ownCommandsAwaitingExecution.Count < PendingExecutionPruneThreshold) return;

        double cutoffMs = now - PendingExecutionExpiryMs;
        var expiredCommands = ownCommandsAwaitingExecution
            .Where(entry => entry.Value < cutoffMs)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var expiredCommand in expiredCommands)
            ownCommandsAwaitingExecution.Remove(expiredCommand);
    }

    private sealed class SentCommand
    {
        public readonly OwnCommandSignature signature;
        public readonly double sentAtMs;

        public SentCommand(OwnCommandSignature signature, double sentAtMs)
        {
            this.signature = signature;
            this.sentAtMs = sentAtMs;
        }
    }
}
