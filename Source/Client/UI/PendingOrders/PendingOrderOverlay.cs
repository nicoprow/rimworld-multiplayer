using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Multiplayer.Client;

internal abstract class PendingOrderOverlay(Map map)
{
    public const float PendingOpacity = 0.5f;
    public static readonly Color PendingCellColor = new(1f, 1f, 1f, PendingOpacity);
    public static readonly Color PendingGhostColor = new(0.5f, 0.8f, 1f, PendingOpacity);

    public readonly Map map = map;

    public abstract void Draw();
}

internal sealed class DesignationIconsOverlay(Map map, Material icon, List<Vector3> iconPositions) : PendingOrderOverlay(map)
{
    private readonly Material fadedIcon = FadedMaterialPool.FadedVersionOf(icon, PendingOpacity);

    public static DesignationIconsOverlay ForCells(Map map, Material icon, IEnumerable<IntVec3> cells)
    {
        float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
        var positions = cells.Select(cell => cell.ToVector3ShiftedWithAltitude(altitude)).ToList();
        return new DesignationIconsOverlay(map, icon, positions);
    }

    public static DesignationIconsOverlay ForThing(Map map, Material icon, Thing thing)
    {
        var position = thing.DrawPos;
        position.y = AltitudeLayer.MetaOverlays.AltitudeFor();
        return new DesignationIconsOverlay(map, icon, [position]);
    }

    public override void Draw()
    {
        foreach (var position in iconPositions)
            Graphics.DrawMesh(MeshPool.plane10, position, Quaternion.identity, fadedIcon, 0);
    }
}

internal sealed class PlacementGhostsOverlay(Map map, ThingDef thingDef, ThingDef stuffDef, Rot4 rotation, List<IntVec3> cells)
    : PendingOrderOverlay(map)
{
    public override void Draw()
    {
        foreach (var cell in cells)
            GhostDrawer.DrawGhostThing(cell, rotation, thingDef, null, PendingGhostColor, AltitudeLayer.Blueprint,
                drawPlaceWorkers: false, stuff: stuffDef);
    }
}

internal sealed class CellFieldOverlay(Map map, List<IntVec3> cells) : PendingOrderOverlay(map)
{
    public override void Draw()
    {
        GenDraw.DrawFieldEdges(cells, PendingCellColor);
    }
}

internal sealed class DraftedMoveOverlay(Map map, Pawn pawn, IntVec3 destination) : PendingOrderOverlay(map)
{
    public override void Draw()
    {
        if (!pawn.Spawned || pawn.Map != map) return;

        var destinationCenter = destination.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
        GenDraw.DrawLineBetween(pawn.DrawPos, destinationCenter);
        GenDraw.DrawTargetHighlight(new LocalTargetInfo(destination));
    }
}
