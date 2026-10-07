namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_Command)]
public record struct ServerCommandPacket : IPacket
{
    public int index;
    public CommandType type;
    public int ticks;
    public int factionId;
    public int mapId;
    public int playerId;
    public byte[] data;

    public static ServerCommandPacket From(ScheduledCommand cmd, int index) => new()
    {
        index = index,
        type = cmd.type,
        ticks = cmd.ticks,
        factionId = cmd.factionId,
        mapId = cmd.mapId,
        playerId = cmd.playerId,
        data = cmd.data
    };

    public ScheduledCommand ToCommand() => new(type: type, ticks: ticks, factionId: factionId, mapId: mapId,
        playerId: playerId, data: data);

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref index);
        buf.BindEnum(ref type);
        buf.Bind(ref ticks);
        buf.Bind(ref factionId);
        buf.Bind(ref mapId);
        buf.Bind(ref playerId);
        buf.BindRemaining(ref data, maxLength: 65535);
    }
}

[PacketDefinition(Packets.Client_Command)]
public record struct ClientCommandPacket(CommandType type, int mapId, byte[] data) : IPacket
{
    public int index;
    public CommandType type = type;
    public int mapId = mapId;
    public byte[] data = data;

    public static ClientCommandPacket FromPayload(byte[] payload)
    {
        var packet = new ClientCommandPacket();
        packet.Bind(new PacketReader(new ByteReader(payload)));
        return packet;
    }

    public byte[] ToPayload() => this.Serialize().data;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref index);
        buf.BindEnum(ref type);
        buf.Bind(ref mapId);
        buf.BindRemaining(ref data, maxLength: 65535);
    }
}
