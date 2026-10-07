using System.Collections.Generic;

namespace Multiplayer.Common.Networking.Packet;

public record struct RedundantCommand(int index, byte[] payload) : IPacketBufferable
{
    public const int EncodingOverheadBytes = 8;
    public const int MaxPayloadLength = 65535;

    public int index = index;
    public byte[] payload = payload;

    public int EncodedSize => EncodingOverheadBytes + payload.Length;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref index);
        buf.BindBytes(ref payload, maxLength: MaxPayloadLength);
    }
}

[PacketDefinition(Packets.Client_RedundantCommands)]
public record struct ClientRedundantCommandsPacket(List<RedundantCommand> commands) : IPacket
{
    public const int MaxCommandsPerPacket = 1024;

    public List<RedundantCommand> commands = commands;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref commands, BinderOf.Identity<RedundantCommand>(), maxLength: MaxCommandsPerPacket);
    }
}
