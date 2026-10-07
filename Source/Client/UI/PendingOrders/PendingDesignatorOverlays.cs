using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
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

        if (TryGetDesignatorIcon(designator, out var designatorIcon))
        {
            var affectedPositions = PositionsAffectedByCellDesignation(designator, map, cellList);
            if (affectedPositions.Count > 0)
                return new DesignationIconsOverlay(map, designatorIcon, affectedPositions);
        }

        return new CellFieldOverlay(map, cellList);
    }

    public static PendingOrderOverlay ForThing(Designator designator, Map map, Thing thing)
    {
        if (designator.Designation?.iconMat is { } designationIcon)
            return DesignationIconsOverlay.ForThing(map, designationIcon, thing);

        if (TryGetDesignatorIcon(designator, out var designatorIcon))
            return DesignationIconsOverlay.ForThing(map, designatorIcon, thing);

        return new CellFieldOverlay(map, thing.OccupiedRect().Cells.ToList());
    }

    private static bool TryGetDesignatorIcon(Designator designator, out Material icon)
    {
        icon = designator.icon is Texture2D iconTexture
            ? MaterialPool.MatFrom(iconTexture, ShaderDatabase.MetaOverlay, Color.white)
            : null;
        return icon != null;
    }

    private static List<Vector3> PositionsAffectedByCellDesignation(Designator designator, Map map, List<IntVec3> cells)
    {
        float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();

        var affectedThingPositions = cells
            .SelectMany(cell => cell.GetThingList(map))
            .Distinct()
            .Where(thing => designator.CanDesignateThing(thing).Accepted)
            .Select(thing => WithAltitude(thing.DrawPos, altitude));

        var cancelledCellDesignationPositions = designator is Designator_Cancel cancelDesignator
            ? cells
                .Where(cell => cancelDesignator.CancelableDesignationsAt(cell).Any(designation => !designation.target.HasThing))
                .Select(cell => cell.ToVector3ShiftedWithAltitude(altitude))
            : Enumerable.Empty<Vector3>();

        return affectedThingPositions.Concat(cancelledCellDesignationPositions).ToList();
    }

    private static Vector3 WithAltitude(Vector3 position, float altitude)
    {
        position.y = altitude;
        return position;
    }
}
