using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.Client.Util;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Building_Bed), nameof(Building_Bed.Medical), MethodType.Setter)]
static class RecordPendingMedicalBed
{
    static void Prefix(Building_Bed __instance, bool value)
    {
        if (!Multiplayer.ShouldSync) return;

        PendingBuildingFields.Medical.AttachToNextOwnCommand(__instance, value);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(CompGatherSpot), nameof(CompGatherSpot.Active), MethodType.Setter)]
static class RecordPendingGatherSpotActive
{
    static void Prefix(CompGatherSpot __instance, bool value)
    {
        if (!Multiplayer.ShouldSync) return;

        PendingBuildingFields.GatherSpotActive.AttachToNextOwnCommand(__instance, value);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.Priority), MethodType.Setter)]
static class RecordPendingStoragePriority
{
    static void Prefix(StorageSettings __instance, StoragePriority value)
    {
        if (!Multiplayer.ShouldSync) return;

        PendingBuildingFields.StoragePriority.AttachToNextOwnCommand(__instance, value);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(CompTempControl), nameof(CompTempControl.InterfaceChangeTargetTemperature))]
static class RecordPendingTargetTemperatureChange
{
    static void Prefix(CompTempControl __instance, float offset)
    {
        if (!Multiplayer.ShouldSync) return;

        float shownTemperature = PendingBuildingFields.TargetTemperature.ShownValueOf(__instance);
        float pendingTemperature = PendingBuildingFields.ClampedTargetTemperature(shownTemperature + offset);
        PendingBuildingFields.TargetTemperature.AttachToNextOwnCommand(__instance, pendingTemperature);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class RecordPendingTargetTemperatureReset
{
    static MethodBase TargetMethod() =>
        MpMethodUtil.GetLambda(typeof(CompTempControl), nameof(CompTempControl.CompGetGizmosExtra), lambdaOrdinal: 2);

    static void Prefix(CompTempControl __instance)
    {
        if (!Multiplayer.ShouldSync) return;

        float defaultTemperature = __instance.Props.defaultTargetTemperature;
        PendingBuildingFields.TargetTemperature.AttachToNextOwnCommand(__instance, defaultTemperature);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class RecordPendingFlippedBuildingToggle
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return MpMethodUtil.GetLambda(typeof(CompFlickable), nameof(CompFlickable.CompGetGizmosExtra), lambdaOrdinal: 1);
        yield return MpMethodUtil.GetLambda(typeof(Building_TurretGun), nameof(Building_TurretGun.GetGizmos), lambdaOrdinal: 2);
        yield return MpMethodUtil.GetLambda(typeof(Building_Trap), nameof(Building_Trap.GetGizmos), lambdaOrdinal: 1);
        yield return MpMethodUtil.GetLambda(typeof(Building_Door), nameof(Building_Door.GetGizmos), lambdaOrdinal: 1);
    }

    static void Prefix(object __instance)
    {
        if (!Multiplayer.ShouldSync) return;

        switch (__instance)
        {
            case CompFlickable flickable:
                PendingBuildingFields.WantSwitchOn.AttachFlippedToNextOwnCommand(flickable);
                break;
            case Building_TurretGun turret:
                PendingBuildingFields.HoldFire.AttachFlippedToNextOwnCommand(turret);
                break;
            case Building_Trap trap:
                PendingBuildingFields.AutoRearm.AttachFlippedToNextOwnCommand(trap);
                break;
            case Building_Door door:
                PendingBuildingFields.HoldOpen.AttachFlippedToNextOwnCommand(door);
                break;
        }
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class RecordPendingBedOwnerType
{
    private static FieldInfo ownerTypeField;
    private static FieldInfo bedsToAffectField;

    static MethodBase TargetMethod()
    {
        var setOwnerTypeOfBeds = MpMethodUtil.GetLambda(typeof(Building_Bed), nameof(Building_Bed.SetBedOwnerTypeByInterface), lambdaOrdinal: 0);
        ownerTypeField = AccessTools.Field(setOwnerTypeOfBeds.DeclaringType, "ownerType");
        bedsToAffectField = AccessTools.Field(setOwnerTypeOfBeds.DeclaringType, "bedsToAffect");
        return setOwnerTypeOfBeds;
    }

    static void Prefix(object __instance)
    {
        if (!Multiplayer.ShouldSync) return;

        var ownerType = (BedOwnerType)ownerTypeField.GetValue(__instance);
        var bedsToAffect = bedsToAffectField.GetValue(__instance) as IEnumerable<Building_Bed>;
        var affectedBeds = bedsToAffect?.Where(bed => bed != null).ToList();
        if (affectedBeds.NullOrEmpty()) return;

        PendingBuildingFields.OwnerType.AttachToNextOwnCommand(affectedBeds, ownerType);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class RecordPendingOwnerChange
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var assignableType in typeof(CompAssignableToPawn).AllSubtypesAndSelf())
        {
            var assignMethod = assignableType.GetMethod(nameof(CompAssignableToPawn.TryAssignPawn), AccessTools.allDeclared, null, [typeof(Pawn)], null);
            var unassignMethod = assignableType.GetMethod(nameof(CompAssignableToPawn.TryUnassignPawn), AccessTools.allDeclared, null, [typeof(Pawn), typeof(bool), typeof(bool)], null);

            if (assignMethod != null) yield return assignMethod;
            if (unassignMethod != null) yield return unassignMethod;
        }
    }

    static void Prefix(CompAssignableToPawn __instance, Pawn pawn, MethodBase __originalMethod)
    {
        if (!Multiplayer.ShouldSync || pawn == null) return;

        bool assigned = __originalMethod.Name == nameof(CompAssignableToPawn.TryAssignPawn);
        PendingOwners.AttachToNextOwnCommand(__instance, pawn, assigned);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch]
static class ShowPendingBuildingValuesWhileDrawing
{
    static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGridFor));
        yield return AccessTools.Method(typeof(MainTabWindow_Inspect), nameof(MainTabWindow_Inspect.DoWindowContents));
        yield return AccessTools.Method(typeof(ITab_Storage), nameof(ITab_Storage.FillTab));
        yield return AccessTools.Method(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI));
        yield return AccessTools.Method(typeof(Dialog_AssignBuildingOwner), nameof(Dialog_AssignBuildingOwner.DoWindowContents));
        yield return AccessTools.Method(typeof(Building_Bed), nameof(Building_Bed.SetBedOwnerTypeByInterface));
    }

    static void Prefix(out PendingValuesShownWhileDrawing __state)
    {
        __state = null;
        if (!PendingValuesShownWhileDrawing.ShouldShowPendingValues()) return;

        __state = PendingBuildingFields.ShowAllPendingValues();
    }

    static Exception Finalizer(Exception __exception, PendingValuesShownWhileDrawing __state)
    {
        __state?.RestoreRealValues();
        return __exception;
    }
}

static class PendingBuildingFields
{
    private const float MinTargetTemperature = -273.15f;
    private const float MaxTargetTemperature = 1000f;

    public static readonly PendingField<Building_Bed, bool> Medical = new(
        nameof(Building_Bed.Medical), MapOfThing,
        bed => bed.medicalInt, (bed, medical) => bed.medicalInt = medical);

    public static readonly PendingField<Building_Bed, BedOwnerType> OwnerType = new(
        nameof(Building_Bed.ForOwnerType), MapOfThing,
        bed => bed.forOwnerType, (bed, ownerType) => bed.forOwnerType = ownerType);

    public static readonly PendingField<CompGatherSpot, bool> GatherSpotActive = new(
        nameof(CompGatherSpot.Active), MapOfComp,
        gatherSpot => gatherSpot.active, (gatherSpot, active) => gatherSpot.active = active);

    public static readonly PendingField<CompFlickable, bool> WantSwitchOn = new(
        nameof(CompFlickable.wantSwitchOn), MapOfComp,
        flickable => flickable.wantSwitchOn, (flickable, wantOn) => flickable.wantSwitchOn = wantOn);

    public static readonly PendingField<Building_TurretGun, bool> HoldFire = new(
        nameof(Building_TurretGun.holdFire), MapOfThing,
        turret => turret.holdFire, (turret, holdFire) => turret.holdFire = holdFire);

    public static readonly PendingField<Building_Trap, bool> AutoRearm = new(
        nameof(Building_Trap.autoRearm), MapOfThing,
        trap => trap.autoRearm, (trap, autoRearm) => trap.autoRearm = autoRearm);

    public static readonly PendingField<Building_Door, bool> HoldOpen = new(
        nameof(Building_Door.holdOpenInt), MapOfThing,
        door => door.holdOpenInt, (door, holdOpen) => door.holdOpenInt = holdOpen);

    public static readonly PendingField<CompTempControl, float> TargetTemperature = new(
        nameof(CompTempControl.targetTemperature), MapOfComp,
        tempControl => tempControl.targetTemperature, (tempControl, temperature) => tempControl.targetTemperature = temperature);

    public static readonly PendingField<StorageSettings, StoragePriority> StoragePriority = new(
        nameof(StorageSettings.Priority), MapOfStorageSettings,
        settings => settings.priorityInt, (settings, priority) => settings.priorityInt = priority);

    private static readonly IPendingField[] allFields =
        [Medical, OwnerType, GatherSpotActive, WantSwitchOn, HoldFire, AutoRearm, HoldOpen, TargetTemperature, StoragePriority];

    public static float ClampedTargetTemperature(float temperature) =>
        UnityEngine.Mathf.Clamp(temperature, MinTargetTemperature, MaxTargetTemperature);

    public static PendingValuesShownWhileDrawing ShowAllPendingValues()
    {
        var shown = new PendingValuesShownWhileDrawing();
        foreach (var field in allFields)
            field.ShowAllPendingValuesIn(shown);

        PendingOwners.ShowAllPendingOwnersIn(shown);
        return shown;
    }

    private static Map MapOfThing(Thing thing) => thing.MapHeld;

    private static Map MapOfComp(ThingComp comp) => comp.parent.MapHeld;

    private static Map MapOfStorageSettings(StorageSettings settings) => settings.owner switch
    {
        Thing thing => thing.MapHeld,
        ThingComp comp => comp.parent.MapHeld,
        Zone zone => zone.Map,
        _ => Find.CurrentMap
    };
}

interface IPendingField
{
    void ShowAllPendingValuesIn(PendingValuesShownWhileDrawing shown);
}

sealed class PendingField<TTarget, TValue>(
    string valueName,
    Func<TTarget, Map> mapOf,
    Func<TTarget, TValue> read,
    Action<TTarget, TValue> write) : IPendingField where TTarget : class
{
    public void AttachToNextOwnCommand(TTarget target, TValue pendingValue)
    {
        var pendingOverride = new PendingValueOverride(mapOf(target), target, valueName, pendingValue);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingOverride);
    }

    public void AttachToNextOwnCommand(List<TTarget> targets, TValue pendingValue)
    {
        var pendingOverride = new PendingValueOverrideForEach(mapOf(targets[0]), targets.Cast<object>().ToList(), valueName, pendingValue);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingOverride);
    }

    public void AttachFlippedToNextOwnCommand(TTarget target)
    {
        bool shownValue = (bool)(object)ShownValueOf(target);
        AttachToNextOwnCommand(target, (TValue)(object)!shownValue);
    }

    public TValue ShownValueOf(TTarget target) =>
        PendingOrderRegistry.TryGetPendingValue(target, valueName, out TValue pendingValue) ? pendingValue : read(target);

    public void ShowAllPendingValuesIn(PendingValuesShownWhileDrawing shown)
    {
        foreach (var target in PendingOrderRegistry.TargetsWithPendingValue(valueName))
        {
            if (target is not TTarget typedTarget) continue;

            shown.ShowPendingValue(typedTarget, valueName, () => read(typedTarget), value => write(typedTarget, value));
        }
    }
}

sealed class PendingOwnerChange(Map map, CompAssignableToPawn assignable, Pawn pawn, bool assigned)
    : PendingValueOverride(map, assignable, PendingOwners.ValueName, assigned)
{
    public readonly Pawn pawn = pawn;
    public readonly bool assigned = assigned;
}

static class PendingOwners
{
    public const string ValueName = "Owners";

    public static void AttachToNextOwnCommand(CompAssignableToPawn assignable, Pawn pawn, bool assigned)
    {
        var ownerChange = new PendingOwnerChange(assignable.parent.MapHeld, assignable, pawn, assigned);
        PendingOrderRegistry.AttachToNextOwnCommand(ownerChange);
    }

    public static void ShowAllPendingOwnersIn(PendingValuesShownWhileDrawing shown)
    {
        foreach (var target in PendingOrderRegistry.TargetsWithPendingValue(ValueName))
            if (target is CompAssignableToPawn assignable)
                ShowPendingOwnersOf(assignable, shown);
    }

    private static void ShowPendingOwnersOf(CompAssignableToPawn assignable, PendingValuesShownWhileDrawing shown)
    {
        var realOwners = assignable.assignedPawns;
        var shownOwners = new List<Pawn>(realOwners);

        foreach (var pendingOverride in PendingOrderRegistry.PendingValueOverridesInSendOrder(assignable, ValueName))
            if (pendingOverride is PendingOwnerChange ownerChange)
                ApplyOwnerChange(shownOwners, ownerChange, assignable.MaxAssignedPawnsCount);

        assignable.assignedPawns = shownOwners;
        shown.RestoreAfterDrawing(() =>
        {
            bool stillShowingPendingOwners = assignable.assignedPawns == shownOwners;
            if (stillShowingPendingOwners)
                assignable.assignedPawns = realOwners;
        });
    }

    private static void ApplyOwnerChange(List<Pawn> owners, PendingOwnerChange ownerChange, int maxOwners)
    {
        if (!ownerChange.assigned)
        {
            owners.Remove(ownerChange.pawn);
            return;
        }

        if (owners.Contains(ownerChange.pawn)) return;

        bool allSlotsTaken = owners.Count > 0 && owners.Count >= maxOwners;
        if (allSlotsTaken)
            owners.RemoveAt(owners.Count - 1);

        owners.Add(ownerChange.pawn);
    }
}
