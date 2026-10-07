using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch]
static class RecordPendingAssignments
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertySetter(typeof(Pawn_FoodRestrictionTracker), nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy));
        yield return AccessTools.PropertySetter(typeof(Pawn_OutfitTracker), nameof(Pawn_OutfitTracker.CurrentApparelPolicy));
        yield return AccessTools.PropertySetter(typeof(Pawn_DrugPolicyTracker), nameof(Pawn_DrugPolicyTracker.CurrentPolicy));
        yield return AccessTools.PropertySetter(typeof(Pawn_ReadingTracker), nameof(Pawn_ReadingTracker.CurrentPolicy));
        yield return AccessTools.PropertySetter(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap));
    }

    static void Prefix(object __instance, object value, MethodBase __originalMethod)
    {
        if (!Multiplayer.ShouldSync) return;
        if (PendingAssignments.PawnOf(__instance) is not { } pawn) return;

        string valueName = PendingAssignments.ValueNameOf(__originalMethod);
        var pendingAssignment = new PendingValueOverride(pawn.MapHeld, __instance, valueName, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingAssignment);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class ShowPendingAssignmentsWhileDrawing
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PawnColumnWorker_FoodRestriction), nameof(PawnColumnWorker.DoCell));
        yield return AccessTools.Method(typeof(PawnColumnWorker_Outfit), nameof(PawnColumnWorker.DoCell));
        yield return AccessTools.Method(typeof(PawnColumnWorker_DrugPolicy), nameof(PawnColumnWorker.DoCell));
        yield return AccessTools.Method(typeof(PawnColumnWorker_Reading), nameof(PawnColumnWorker.DoCell));
        yield return AccessTools.Method(typeof(PawnColumnWorker_AllowedArea), nameof(PawnColumnWorker.DoCell));
        yield return AccessTools.Method(typeof(InspectPaneFiller), nameof(InspectPaneFiller.DrawAreaAllowed));
    }

    static void Prefix(Pawn pawn, out PendingAssignmentsShownWhileDrawing __state)
    {
        __state = null;
        if (Multiplayer.Client == null || TickPatch.Simulating) return;
        if (PendingOrderRegistry.Count == 0) return;

        __state = PendingAssignmentsShownWhileDrawing.Show(pawn);
    }

    static Exception Finalizer(Exception __exception, PendingAssignmentsShownWhileDrawing __state)
    {
        __state?.RestoreRealAssignments();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Getter)]
static class ShowPendingAllowedAreaWhileDrawing
{
    static void Postfix(Pawn_PlayerSettings __instance, ref Area __result)
    {
        if (PendingAssignmentsShownWhileDrawing.TryGetPendingAllowedAreaWhileDrawing(__instance, out var pendingArea))
            __result = pendingArea;
    }
}

static class PendingAssignments
{
    public const string FoodPolicy = nameof(Pawn_FoodRestrictionTracker.CurrentFoodPolicy);
    public const string ApparelPolicy = nameof(Pawn_OutfitTracker.CurrentApparelPolicy);
    public const string DrugPolicy = nameof(Pawn_DrugPolicyTracker.CurrentPolicy);
    public const string ReadingPolicy = nameof(Pawn_ReadingTracker.CurrentPolicy);
    public const string AllowedArea = nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap);

    public static Pawn PawnOf(object assignmentTracker) => assignmentTracker switch
    {
        Pawn_FoodRestrictionTracker foodTracker => foodTracker.pawn,
        Pawn_OutfitTracker outfitTracker => outfitTracker.pawn,
        Pawn_DrugPolicyTracker drugTracker => drugTracker.pawn,
        Pawn_ReadingTracker readingTracker => readingTracker.pawn,
        Pawn_PlayerSettings playerSettings => playerSettings.pawn,
        _ => null
    };

    public static string ValueNameOf(MethodBase setter) => setter.Name.Substring("set_".Length);
}

sealed class PendingAssignmentsShownWhileDrawing
{
    private readonly List<Action> restoreRealValues = new();

    public static PendingAssignmentsShownWhileDrawing Show(Pawn pawn)
    {
        var shown = new PendingAssignmentsShownWhileDrawing();

        if (pawn.foodRestriction is { } foodTracker)
            shown.ShowPendingPolicy(foodTracker, PendingAssignments.FoodPolicy,
                () => foodTracker.curPolicy, policy => foodTracker.curPolicy = policy);

        if (pawn.outfits is { } outfitTracker)
            shown.ShowPendingPolicy(outfitTracker, PendingAssignments.ApparelPolicy,
                () => outfitTracker.curApparelPolicy, policy => outfitTracker.curApparelPolicy = policy);

        if (pawn.drugs is { } drugTracker)
            shown.ShowPendingPolicy(drugTracker, PendingAssignments.DrugPolicy,
                () => drugTracker.curPolicy, policy => drugTracker.curPolicy = policy);

        if (pawn.reading is { } readingTracker)
            shown.ShowPendingPolicy(readingTracker, PendingAssignments.ReadingPolicy,
                () => readingTracker.curPolicy, policy => readingTracker.curPolicy = policy);

        shown.ShowPendingAllowedAreaOf(pawn);

        return shown;
    }

    public void RestoreRealAssignments()
    {
        for (int restoreIndex = restoreRealValues.Count - 1; restoreIndex >= 0; restoreIndex--)
            restoreRealValues[restoreIndex]();

        restoreRealValues.Clear();
    }

    public static bool TryGetPendingAllowedAreaWhileDrawing(Pawn_PlayerSettings playerSettings, out Area pendingArea)
    {
        pendingArea = null;
        if (pawnWithPendingAllowedAreaShown == null || playerSettings.pawn != pawnWithPendingAllowedAreaShown) return false;

        return PendingOrderRegistry.TryGetPendingValue(playerSettings, PendingAssignments.AllowedArea, out pendingArea);
    }

    private static Pawn pawnWithPendingAllowedAreaShown;

    private void ShowPendingPolicy<TPolicy>(object tracker, string valueName, Func<TPolicy> readPolicy, Action<TPolicy> writePolicy)
        where TPolicy : Policy
    {
        if (!PendingOrderRegistry.TryGetPendingValue(tracker, valueName, out TPolicy pendingPolicy)) return;
        if (pendingPolicy == null) return;

        var realPolicy = readPolicy();
        writePolicy(pendingPolicy);
        restoreRealValues.Add(() => writePolicy(realPolicy));
    }

    private void ShowPendingAllowedAreaOf(Pawn pawn)
    {
        var previouslyShownPawn = pawnWithPendingAllowedAreaShown;
        pawnWithPendingAllowedAreaShown = pawn;
        restoreRealValues.Add(() => pawnWithPendingAllowedAreaShown = previouslyShownPawn);
    }
}
