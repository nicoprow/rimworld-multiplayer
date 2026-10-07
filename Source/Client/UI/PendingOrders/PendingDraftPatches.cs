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

        var pendingDraftState = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingDraftState.ValueName, value);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingDraftState);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.GetGizmos))]
static class ShowPendingDraftStateOnGizmo
{
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn_DraftController __instance)
    {
        foreach (var gizmo in gizmos)
        {
            bool isDraftToggle = gizmo is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ColonistDraft;
            if (Multiplayer.Client != null && isDraftToggle)
                PendingDraftState.ShowOn((Command_Toggle)gizmo, __instance);

            yield return gizmo;
        }
    }
}

static class PendingDraftState
{
    public const string ValueName = nameof(Pawn_DraftController.Drafted);

    public static void ShowOn(Command_Toggle draftToggle, Pawn_DraftController draftController)
    {
        var originalToggleAction = draftToggle.toggleAction;

        draftToggle.isActive = () => DisplayedDraftState(draftController);
        draftToggle.toggleAction = () =>
        {
            if (TryGetPending(draftController, out bool pendingDrafted))
                draftController.Drafted = !pendingDrafted;
            else
                originalToggleAction();
        };
    }

    private static bool DisplayedDraftState(Pawn_DraftController draftController) =>
        TryGetPending(draftController, out bool pendingDrafted) ? pendingDrafted : draftController.Drafted;

    private static bool TryGetPending(Pawn_DraftController draftController, out bool pendingDrafted) =>
        PendingOrderRegistry.TryGetPendingValue(draftController, ValueName, out pendingDrafted);
}
