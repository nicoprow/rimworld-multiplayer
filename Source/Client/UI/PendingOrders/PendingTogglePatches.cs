using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
static class RecordPendingDraftState
{
    static void Prefix(Pawn_DraftController __instance, bool value)
    {
        if (!Multiplayer.ShouldSync) return;

        var pendingDraftState = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingToggleValues.Drafted, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingDraftState);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.FireAtWill), MethodType.Setter)]
static class RecordPendingFireAtWillState
{
    static void Prefix(Pawn_DraftController __instance, bool value)
    {
        if (!Multiplayer.ShouldSync) return;

        var pendingFireAtWillState = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingToggleValues.FireAtWill, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingFireAtWillState);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
static class RecordPendingForbiddenState
{
    static void Prefix(CompForbiddable __instance, bool value)
    {
        if (!Multiplayer.ShouldSync) return;

        var pendingForbiddenState = new PendingForbiddenState(__instance.parent.MapHeld, __instance, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingForbiddenState);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.GetGizmos))]
static class ShowPendingDraftControllerStatesOnGizmos
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn_DraftController __instance)
    {
        foreach (var gizmo in gizmos)
        {
            if (Multiplayer.Client != null && gizmo is Command_Toggle toggle)
                ShowPendingStateOn(toggle, __instance);

            yield return gizmo;
        }
    }

    private static void ShowPendingStateOn(Command_Toggle toggle, Pawn_DraftController draftController)
    {
        bool isDraftToggle = toggle.hotKey == KeyBindingDefOf.Command_ColonistDraft;
        bool isFireAtWillToggle = toggle.icon == TexCommand.FireAtWill;

        if (isDraftToggle)
            PendingToggleDisplay.ShowOn(toggle, draftController, PendingToggleValues.Drafted,
                () => draftController.Drafted, drafted => draftController.Drafted = drafted);
        else if (isFireAtWillToggle)
            PendingToggleDisplay.ShowOn(toggle, draftController, PendingToggleValues.FireAtWill,
                () => draftController.FireAtWill, fireAtWill => draftController.FireAtWill = fireAtWill);
    }
}

[HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.CompGetGizmosExtra))]
static class ShowPendingForbiddenStateOnGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, CompForbiddable __instance)
    {
        foreach (var gizmo in gizmos)
        {
            bool isForbidToggle = gizmo is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ItemForbid;
            if (Multiplayer.Client != null && isForbidToggle)
                PendingToggleDisplay.ShowOn((Command_Toggle)gizmo, __instance, PendingToggleValues.Forbidden,
                    () => __instance.Forbidden, forbidden => __instance.Forbidden = forbidden);

            yield return gizmo;
        }
    }
}

[HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawOverlay))]
static class ShowPendingForbiddenStateOnOverlay
{
    static void Prefix(Thing t, ref OverlayTypes overlayType)
    {
        if (Multiplayer.Client == null || PendingOrderRegistry.Count == 0) return;
        if (t.TryGetComp<CompForbiddable>() is not { } forbiddable) return;
        if (!PendingOrderRegistry.TryGetPendingValue(forbiddable, PendingToggleValues.Forbidden, out bool pendingForbidden)) return;

        overlayType = PendingForbiddenState.OverlayTypesShowing(pendingForbidden, overlayType, forbiddable);
    }
}

static class PendingToggleValues
{
    public const string Drafted = nameof(Pawn_DraftController.Drafted);
    public const string FireAtWill = nameof(Pawn_DraftController.FireAtWill);
    public const string Forbidden = nameof(CompForbiddable.Forbidden);
}

static class PendingToggleDisplay
{
    public static void ShowOn(Command_Toggle toggle, object target, string valueName, Func<bool> realValue, Action<bool> setValue)
    {
        var originalIsActive = toggle.isActive;
        var originalToggleAction = toggle.toggleAction;
        bool isActiveMatchesValue = originalIsActive() == realValue();

        toggle.isActive = () =>
        {
            if (!TryGetPending(target, valueName, out bool pendingValue))
                return originalIsActive();

            return isActiveMatchesValue ? pendingValue : !pendingValue;
        };

        toggle.toggleAction = () =>
        {
            if (TryGetPending(target, valueName, out bool pendingValue))
                setValue(!pendingValue);
            else
                originalToggleAction();
        };
    }

    private static bool TryGetPending(object target, string valueName, out bool pendingValue) =>
        PendingOrderRegistry.TryGetPendingValue(target, valueName, out pendingValue);
}
