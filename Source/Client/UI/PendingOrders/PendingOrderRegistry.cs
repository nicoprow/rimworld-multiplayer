using System.Collections.Generic;
using Multiplayer.Common;
using UnityEngine;
using Verse;

namespace Multiplayer.Client;

internal static class PendingOrderRegistry
{
    public const float ExpiryAfterSendSeconds = 10f;

    private static readonly List<PendingOrder> ordersAwaitingExecution = new();
    private static PendingOrderOverlay overlayForNextOwnCommand;

    public static int Count => ordersAwaitingExecution.Count;

    public static void AttachToNextOwnCommand(PendingOrderOverlay overlay)
    {
        overlayForNextOwnCommand = overlay;
    }

    public static void StopAttaching()
    {
        overlayForNextOwnCommand = null;
    }

    public static void Notify_OwnCommandSent(CommandType type, int mapId, byte[] data)
    {
        if (overlayForNextOwnCommand == null) return;

        var signature = new OwnCommandSignature(type, mapId, data);
        ordersAwaitingExecution.Add(new PendingOrder(signature, overlayForNextOwnCommand, Time.realtimeSinceStartup));
        overlayForNextOwnCommand = null;
    }

    public static void Notify_CommandExecuted(ScheduledCommand cmd)
    {
        if (ordersAwaitingExecution.Count == 0) return;
        if (!cmd.IsIssuedBySelf()) return;

        int executedOrderIndex = ordersAwaitingExecution.FindIndex(order => order.signature.Matches(cmd));
        if (executedOrderIndex >= 0)
            ordersAwaitingExecution.RemoveAt(executedOrderIndex);
    }

    public static bool TryGetPendingValue<T>(object target, string valueName, out T pendingValue)
    {
        for (int orderIndex = ordersAwaitingExecution.Count - 1; orderIndex >= 0; orderIndex--)
        {
            if (ordersAwaitingExecution[orderIndex].overlay is not PendingValueOverride valueOverride) continue;
            if (!valueOverride.Overrides(target, valueName)) continue;

            if (valueOverride.pendingValue is T typedValue)
            {
                pendingValue = typedValue;
                return true;
            }

            bool pendingValueIsNullReference = valueOverride.pendingValue == null && default(T) == null;
            if (pendingValueIsNullReference)
            {
                pendingValue = default;
                return true;
            }
        }

        pendingValue = default;
        return false;
    }

    public static List<object> TargetsWithPendingValue(string valueName)
    {
        var targets = new List<object>();
        foreach (var order in ordersAwaitingExecution)
            if (order.overlay is PendingValueOverride valueOverride && valueOverride.valueName == valueName)
                targets.AddRange(valueOverride.Targets);

        return targets;
    }

    public static List<PendingValueOverride> PendingValueOverridesInSendOrder(object target, string valueName)
    {
        var overrides = new List<PendingValueOverride>();
        foreach (var order in ordersAwaitingExecution)
            if (order.overlay is PendingValueOverride valueOverride && valueOverride.Overrides(target, valueName))
                overrides.Add(valueOverride);

        return overrides;
    }

    public static void DrawOrdersOn(Map map)
    {
        RemoveExpiredOrders();

        foreach (var order in ordersAwaitingExecution)
            if (order.overlay.map == map)
                order.overlay.Draw();
    }

    public static void Reset()
    {
        ordersAwaitingExecution.Clear();
        overlayForNextOwnCommand = null;
    }

    private static void RemoveExpiredOrders()
    {
        float expiryCutoff = Time.realtimeSinceStartup - ExpiryAfterSendSeconds;
        ordersAwaitingExecution.RemoveAll(order => order.sentAt < expiryCutoff || order.overlay.map is { Disposed: true });
    }

    private sealed class PendingOrder(OwnCommandSignature signature, PendingOrderOverlay overlay, float sentAt)
    {
        public readonly OwnCommandSignature signature = signature;
        public readonly PendingOrderOverlay overlay = overlay;
        public readonly float sentAt = sentAt;
    }
}
