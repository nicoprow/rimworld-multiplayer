using System;
using System.Collections.Generic;

namespace Multiplayer.Client;

sealed class PendingValuesShownWhileDrawing
{
    private readonly List<Action> restoreRealValues = new();

    public static bool ShouldShowPendingValues() =>
        Multiplayer.InInterface && !TickPatch.Simulating && PendingOrderRegistry.Count > 0;

    public void ShowPendingValue<T>(object target, string valueName, Func<T> readValue, Action<T> writeValue)
    {
        if (!PendingOrderRegistry.TryGetPendingValue(target, valueName, out T pendingValue)) return;

        var realValue = readValue();
        writeValue(pendingValue);
        RestoreAfterDrawing(() => RestoreIfStillShowing(pendingValue, realValue, readValue, writeValue));
    }

    private static void RestoreIfStillShowing<T>(T pendingValue, T realValue, Func<T> readValue, Action<T> writeValue)
    {
        bool valueChangedWhileDrawing = !EqualityComparer<T>.Default.Equals(readValue(), pendingValue);
        if (valueChangedWhileDrawing) return;

        writeValue(realValue);
    }

    public void RestoreAfterDrawing(Action restoreRealValue)
    {
        restoreRealValues.Add(restoreRealValue);
    }

    public void RestoreRealValues()
    {
        for (int restoreIndex = restoreRealValues.Count - 1; restoreIndex >= 0; restoreIndex--)
            restoreRealValues[restoreIndex]();

        restoreRealValues.Clear();
    }
}
