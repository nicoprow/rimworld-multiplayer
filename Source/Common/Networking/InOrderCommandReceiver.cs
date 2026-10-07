using System;
using System.Collections.Generic;

namespace Multiplayer.Common;

public class InOrderCommandReceiver<T>
{
    public const int MaxCommandsAheadOfOrder = 4096;

    private readonly Dictionary<int, T> commandsAheadOfOrder = new();

    public int NextExpectedIndex { get; private set; }

    public int CommandsAheadOfOrderCount => commandsAheadOfOrder.Count;

    public void Reset(int nextExpectedIndex)
    {
        NextExpectedIndex = nextExpectedIndex;
        commandsAheadOfOrder.Clear();
    }

    public void Receive(int index, T command, Action<T> deliverInOrder)
    {
        bool alreadyDelivered = index < NextExpectedIndex;
        if (alreadyDelivered) return;

        bool arrivedAheadOfOrder = index > NextExpectedIndex;
        if (arrivedAheadOfOrder)
        {
            KeepUntilItsTurn(index, command);
            return;
        }

        Deliver(command, deliverInOrder);
        DeliverKeptCommandsWhoseTurnHasCome(deliverInOrder);
    }

    private void KeepUntilItsTurn(int index, T command)
    {
        bool hasRoomForMore = commandsAheadOfOrder.Count < MaxCommandsAheadOfOrder;
        if (hasRoomForMore || commandsAheadOfOrder.ContainsKey(index))
            commandsAheadOfOrder[index] = command;
    }

    private void Deliver(T command, Action<T> deliverInOrder)
    {
        NextExpectedIndex++;
        deliverInOrder(command);
    }

    private void DeliverKeptCommandsWhoseTurnHasCome(Action<T> deliverInOrder)
    {
        while (commandsAheadOfOrder.TryGetValue(NextExpectedIndex, out var nextCommand))
        {
            commandsAheadOfOrder.Remove(NextExpectedIndex);
            Deliver(nextCommand, deliverInOrder);
        }
    }
}
