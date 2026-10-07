using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

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

        PendingOrderRegistry.AttachToNextOwnCommand(new PawnTargetOverlay(pawn.Map, pawn, gotoLoc));
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class ShowPendingOrderedJob
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob));
        yield return AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork));
    }

    static void Prefix(Pawn_JobTracker __instance, Job job)
    {
        if (Multiplayer.Client == null || !Multiplayer.ShouldSync) return;
        if (job?.targetA.IsValid != true) return;

        var pawn = __instance.pawn;
        PendingOrderRegistry.AttachToNextOwnCommand(new PawnTargetOverlay(pawn.Map, pawn, job.targetA));
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}
