using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Master), MethodType.Setter)]
static class RecordPendingMaster
{
    static void Prefix(Pawn_PlayerSettings __instance, Pawn value)
    {
        if (!Multiplayer.ShouldSync) return;

        var pendingMaster = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingAnimalValues.Master, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingMaster);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_TrainingTracker), nameof(Pawn_TrainingTracker.SetWantedRecursive))]
static class RecordPendingTrainingWanted
{
    static void Prefix(Pawn_TrainingTracker __instance, TrainableDef td, bool checkOn)
    {
        if (!Multiplayer.ShouldSync || td == null) return;

        var pendingWanted = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingAnimalValues.WantedNameOf(td), checkOn);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingWanted);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class RecordPendingFollowSetting
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PawnColumnWorker_FollowDrafted), nameof(PawnColumnWorker_FollowDrafted.SetValue));
        yield return AccessTools.Method(typeof(PawnColumnWorker_FollowFieldwork), nameof(PawnColumnWorker_FollowFieldwork.SetValue));
    }

    static void Prefix(PawnColumnWorker __instance, Pawn pawn, bool value)
    {
        if (!Multiplayer.ShouldSync || pawn.playerSettings == null) return;

        string valueName = PendingAnimalValues.FollowSettingNameOf(__instance);
        var pendingFollowSetting = new PendingValueOverride(pawn.MapHeld, pawn.playerSettings, valueName, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingFollowSetting);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class ShowPendingFollowSetting
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PawnColumnWorker_FollowDrafted), nameof(PawnColumnWorker_FollowDrafted.GetValue));
        yield return AccessTools.Method(typeof(PawnColumnWorker_FollowFieldwork), nameof(PawnColumnWorker_FollowFieldwork.GetValue));
    }

    static void Postfix(PawnColumnWorker __instance, Pawn pawn, ref bool __result)
    {
        if (!PendingValuesShownWhileDrawing.ShouldShowPendingValues() || pawn.playerSettings == null) return;

        string valueName = PendingAnimalValues.FollowSettingNameOf(__instance);
        if (PendingOrderRegistry.TryGetPendingValue(pawn.playerSettings, valueName, out bool pendingFollowSetting))
            __result = pendingFollowSetting;
    }
}

[HarmonyPatch(typeof(PawnColumnWorker_Designator), nameof(PawnColumnWorker_Designator.DesignationConfirmed))]
static class RecordPendingAddedAnimalDesignation
{
    static void Prefix(PawnColumnWorker_Designator __instance, Pawn pawn)
    {
        if (!Multiplayer.ShouldSync || pawn == null) return;

        PendingAnimalValues.AttachPendingDesignation(__instance, pawn, true);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(PawnColumnWorker_Designator), nameof(PawnColumnWorker_Designator.SetValue))]
static class RecordPendingRemovedAnimalDesignation
{
    static void Prefix(PawnColumnWorker_Designator __instance, Pawn pawn, bool value)
    {
        if (!Multiplayer.ShouldSync || pawn == null || value) return;

        PendingAnimalValues.AttachPendingDesignation(__instance, pawn, false);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(PawnColumnWorker_Designator), nameof(PawnColumnWorker_Designator.GetValue))]
static class ShowPendingAnimalDesignation
{
    static void Postfix(PawnColumnWorker_Designator __instance, Pawn pawn, ref bool __result)
    {
        if (!PendingValuesShownWhileDrawing.ShouldShowPendingValues() || pawn == null) return;

        string valueName = PendingAnimalValues.DesignationNameOf(__instance);
        if (PendingOrderRegistry.TryGetPendingValue(pawn, valueName, out bool pendingDesignated))
            __result = pendingDesignated;
    }
}

[HarmonyPatch]
static class ShowPendingMasterWhileDrawing
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TrainableUtility), nameof(TrainableUtility.MasterSelectButton));
        yield return AccessTools.Method(typeof(TrainingCardUtility), nameof(TrainingCardUtility.DrawTrainingCard));
    }

    static void Prefix(Pawn pawn, out PendingValuesShownWhileDrawing __state)
    {
        __state = null;
        if (!PendingValuesShownWhileDrawing.ShouldShowPendingValues()) return;
        if (pawn.playerSettings is not { } playerSettings) return;

        __state = new PendingValuesShownWhileDrawing();
        __state.ShowPendingValue(playerSettings, PendingAnimalValues.Master,
            () => playerSettings.master, master => playerSettings.master = master);
    }

    static Exception Finalizer(Exception __exception, PendingValuesShownWhileDrawing __state)
    {
        __state?.RestoreRealValues();
        return __exception;
    }
}

[HarmonyPatch(typeof(TrainingCardUtility), nameof(TrainingCardUtility.DoTrainableCheckbox))]
static class ShowPendingTrainingWantedWhileDrawing
{
    static void Prefix(Pawn pawn, TrainableDef td, out PendingValuesShownWhileDrawing __state)
    {
        __state = null;
        if (!PendingValuesShownWhileDrawing.ShouldShowPendingValues()) return;
        if (pawn.training is not { } training || td == null) return;

        __state = new PendingValuesShownWhileDrawing();
        __state.ShowPendingValue(training, PendingAnimalValues.WantedNameOf(td),
            () => training.wantedTrainables[td], wanted => training.wantedTrainables[td] = wanted);
    }

    static Exception Finalizer(Exception __exception, PendingValuesShownWhileDrawing __state)
    {
        __state?.RestoreRealValues();
        return __exception;
    }
}

static class PendingAnimalValues
{
    public const string Master = nameof(Pawn_PlayerSettings.Master);
    public const string FollowDrafted = nameof(Pawn_PlayerSettings.followDrafted);
    public const string FollowFieldwork = nameof(Pawn_PlayerSettings.followFieldwork);

    private static readonly Dictionary<Def, string> wantedNames = new();
    private static readonly Dictionary<Def, string> designationNames = new();

    public static string WantedNameOf(TrainableDef trainable) => CachedNameOf(wantedNames, "Wanted", trainable);

    public static string DesignationNameOf(PawnColumnWorker_Designator column) =>
        CachedNameOf(designationNames, "Designation", column.DesignationType);

    public static string FollowSettingNameOf(PawnColumnWorker column) =>
        column is PawnColumnWorker_FollowDrafted ? FollowDrafted : FollowFieldwork;

    public static void AttachPendingDesignation(PawnColumnWorker_Designator column, Pawn pawn, bool designated)
    {
        var pendingDesignation = new PendingValueOverride(pawn.MapHeld, pawn, DesignationNameOf(column), designated);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingDesignation);
    }

    private static string CachedNameOf(Dictionary<Def, string> cache, string prefix, Def def)
    {
        if (!cache.TryGetValue(def, out var name))
        {
            name = $"{prefix}/{def.defName}";
            cache[def] = name;
        }

        return name;
    }
}
