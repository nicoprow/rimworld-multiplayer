using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_TimeControl)]
public record struct ServerTimeControlPacket(int tickUntil, int sentCmds, float serverTimePerTick) : IPacket
{
    public const int MaxRedundantCommandsPerPacket = 1024;

    public int tickUntil = tickUntil;
    public int sentCmds = sentCmds;
    public float serverTimePerTick = serverTimePerTick;
    public int acknowledgedClientCommands;
    public List<RedundantCommand> redundantCommands = [];

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref tickUntil);
        buf.Bind(ref sentCmds);
        buf.Bind(ref serverTimePerTick);
        buf.Bind(ref acknowledgedClientCommands);
        buf.Bind(ref redundantCommands, BinderOf.Identity<RedundantCommand>(), maxLength: MaxRedundantCommandsPerPacket);
    }
}
