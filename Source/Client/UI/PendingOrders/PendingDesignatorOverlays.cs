using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Multiplayer.Client;

internal static class PendingDesignatorOverlays
{
    public static PendingOrderOverlay ForCells(Designator designator, Map map, IEnumerable<IntVec3> cells)
    {
        var cellList = cells.ToList();

        if (designator is Designator_Place placeDesignator && placeDesignator.PlacingDef is ThingDef placedThingDef)
            return new PlacementGhostsOverlay(map, placedThingDef, placeDesignator.StuffDef, placeDesignator.placingRot, cellList);

        if (designator.Designation?.iconMat is { } designationIcon)
            return DesignationIconsOverlay.ForCells(map, designationIcon, cellList);

        return new CellFieldOverlay(map, cellList);
    }

    public static PendingOrderOverlay ForThing(Designator designator, Map map, Thing thing)
    {
        if (designator.Designation?.iconMat is { } designationIcon)
            return DesignationIconsOverlay.ForThing(map, designationIcon, thing);

        return new CellFieldOverlay(map, thing.OccupiedRect().Cells.ToList());
    }
}
