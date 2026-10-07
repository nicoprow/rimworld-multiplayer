using System;
using System.Collections.Generic;

namespace Multiplayer.Client;

sealed class PendingValuesShownWhileDrawing
{
    private readonly List<Action> restoreRealValues = new();

    public void ShowPendingValue<T>(object target, string valueName, Func<T> readRealValue, Action<T> writeValue)
    {
        if (!PendingOrderRegistry.TryGetPendingValue(target, valueName, out T pendingValue)) return;

        var realValue = readRealValue();
        writeValue(pendingValue);
        RestoreAfterDrawing(() => writeValue(realValue));
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
