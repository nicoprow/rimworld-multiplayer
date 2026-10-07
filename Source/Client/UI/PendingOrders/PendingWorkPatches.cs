using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
static class RecordPendingWorkPriority
{
    static void Prefix(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
    {
        if (!Multiplayer.ShouldSync || w == null) return;

        var pendingPriority = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingWorkValues.PriorityNameOf(w), priority);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingPriority);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
static class RecordPendingTimeAssignment
{
    static void Prefix(Pawn_TimetableTracker __instance, int hour, TimeAssignmentDef ta)
    {
        if (!Multiplayer.ShouldSync || ta == null) return;
        if (!PendingWorkValues.IsHourOfDay(hour)) return;

        var pendingAssignment = new PendingValueOverride(__instance.pawn.MapHeld, __instance, PendingWorkValues.AssignmentNameOf(hour), ta);
        PendingOrderRegistry.AttachToNextOwnCommand(pendingAssignment);
    }

    static Exception Finalizer(Exception __exception)
    {
        PendingOrderRegistry.StopAttaching();
        return __exception;
    }
}

[HarmonyPatch(typeof(WidgetsWork), nameof(WidgetsWork.DrawWorkBoxFor))]
static class ShowPendingWorkPriorityWhileDrawing
{
    static void Prefix(Pawn p, WorkTypeDef wType, out PendingValuesShownWhileDrawing __state)
    {
        __state = null;
        if (!PendingWorkValues.ShouldShowPendingValues()) return;
        if (p.workSettings is not { } workSettings || wType == null) return;

        __state = new PendingValuesShownWhileDrawing();
        __state.ShowPendingValue(workSettings, PendingWorkValues.PriorityNameOf(wType),
            () => workSettings.priorities[wType], priority => workSettings.priorities[wType] = priority);
    }

    static Exception Finalizer(Exception __exception, PendingValuesShownWhileDrawing __state)
    {
        __state?.RestoreRealValues();
        return __exception;
    }
}

[HarmonyPatch(typeof(PawnColumnWorker_Timetable), nameof(PawnColumnWorker_Timetable.DoCell))]
static class ShowPendingTimeAssignmentsWhileDrawing
{
    static void Prefix(Pawn pawn, out PendingValuesShownWhileDrawing __state)
    {
        __state = null;
        if (!PendingWorkValues.ShouldShowPendingValues()) return;
        if (pawn.timetable is not { } timetable) return;

        __state = new PendingValuesShownWhileDrawing();
        for (int hour = 0; hour < GenDate.HoursPerDay; hour++)
        {
            int shownHour = hour;
            __state.ShowPendingValue(timetable, PendingWorkValues.AssignmentNameOf(shownHour),
                () => timetable.times[shownHour], assignment => timetable.times[shownHour] = assignment);
        }
    }

    static Exception Finalizer(Exception __exception, PendingValuesShownWhileDrawing __state)
    {
        __state?.RestoreRealValues();
        return __exception;
    }
}

static class PendingWorkValues
{
    private static readonly Dictionary<WorkTypeDef, string> priorityNames = new();
    private static readonly string[] assignmentNames = CreateAssignmentNames();

    public static bool ShouldShowPendingValues() =>
        Multiplayer.Client != null && !TickPatch.Simulating && PendingOrderRegistry.Count > 0;

    public static bool IsHourOfDay(int hour) => hour >= 0 && hour < GenDate.HoursPerDay;

    public static string PriorityNameOf(WorkTypeDef workType)
    {
        if (!priorityNames.TryGetValue(workType, out var priorityName))
        {
            priorityName = $"Priority/{workType.defName}";
            priorityNames[workType] = priorityName;
        }

        return priorityName;
    }

    public static string AssignmentNameOf(int hour) => assignmentNames[hour];

    private static string[] CreateAssignmentNames()
    {
        var names = new string[GenDate.HoursPerDay];
        for (int hour = 0; hour < GenDate.HoursPerDay; hour++)
            names[hour] = $"Assignment/{hour}";

        return names;
    }
}
