using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
static class DrawPendingOrders
{
    static void Postfix()
    {
        if (Multiplayer.Client == null || TickPatch.Simulating) return;
        if (Find.CurrentMap is not { } currentMap) return;

        PendingOrderRegistry.DrawOrdersOn(currentMap);
    }
}

[HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedMove), nameof(FloatMenuOptionProvider_DraftedMove.PawnGotoAction))]
static class ShowPendingDraftedMove
{
    static void Prefix(Pawn pawn, IntVec3 gotoLoc)
    {
        if (Multiplayer.Client == null || !Multiplayer.InInterface) return;

        PendingOrderRegistry.AttachToNextOwnCommand(new DraftedMoveOverlay(pawn.Map, pawn, gotoLoc));
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}
