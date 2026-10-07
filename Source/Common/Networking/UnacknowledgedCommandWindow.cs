using System.Collections.Generic;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common;

public class UnacknowledgedCommandWindow
{
    public const int MaxRedundantBytesPerPacket = 1000;
    public const int MaxRetainedCommands = 4096;

    private readonly List<RedundantCommand> commandsInIndexOrder = [];

    public int Count => commandsInIndexOrder.Count;

    public void Add(int index, byte[] payload)
    {
        commandsInIndexOrder.Add(new RedundantCommand(index, payload));

        bool exceedsRetentionLimit = commandsInIndexOrder.Count > MaxRetainedCommands;
        if (exceedsRetentionLimit)
            commandsInIndexOrder.RemoveAt(0);
    }

    public void ForgetCommandsBefore(int firstStillNeededIndex)
    {
        int acknowledgedCount = 0;
        while (acknowledgedCount < commandsInIndexOrder.Count &&
               commandsInIndexOrder[acknowledgedCount].index < firstStillNeededIndex)
            acknowledgedCount++;

        commandsInIndexOrder.RemoveRange(0, acknowledgedCount);
    }

    public List<RedundantCommand> SelectForPacket(int firstStillNeededIndex)
    {
        var selected = new List<RedundantCommand>();
        int remainingBytes = MaxRedundantBytesPerPacket;

        foreach (var command in commandsInIndexOrder)
        {
            if (command.index < firstStillNeededIndex) continue;

            bool fitsIntoPacket = command.EncodedSize <= remainingBytes;
            if (!fitsIntoPacket) continue;

            selected.Add(command);
            remainingBytes -= command.EncodedSize;
        }

        return selected;
    }
}
