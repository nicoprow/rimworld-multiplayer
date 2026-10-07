using System;
using System.Linq;
using Multiplayer.Common;

namespace Multiplayer.Client;

internal sealed class OwnCommandSignature
{
    private readonly CommandType type;
    private readonly int mapId;
    private readonly byte[] data;

    public OwnCommandSignature(CommandType type, int mapId, byte[] data)
    {
        this.type = type;
        this.mapId = mapId;
        this.data = data ?? Array.Empty<byte>();
    }

    public bool Matches(ScheduledCommand cmd) =>
        cmd.type == type &&
        cmd.mapId == mapId &&
        (cmd.data ?? Array.Empty<byte>()).SequenceEqual(data);
}
