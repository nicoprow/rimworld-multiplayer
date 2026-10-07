using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;

namespace Tests;

[TestFixture]
public class InOrderCommandReceiverTest
{
    private InOrderCommandReceiver<string> receiver = null!;
    private List<string> delivered = null!;

    [SetUp]
    public void SetUp()
    {
        receiver = new InOrderCommandReceiver<string>();
        delivered = [];
    }

    private void Receive(int index, string command) => receiver.Receive(index, command, delivered.Add);

    [Test]
    public void DeliversCommandsArrivingInOrder()
    {
        Receive(0, "a");
        Receive(1, "b");

        Assert.That(delivered, Is.EqualTo(new[] { "a", "b" }));
        Assert.That(receiver.NextExpectedIndex, Is.EqualTo(2));
    }

    [Test]
    public void IgnoresDuplicates()
    {
        Receive(0, "a");
        Receive(0, "a");
        Receive(1, "b");
        Receive(0, "a");

        Assert.That(delivered, Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void HoldsCommandsAheadOfOrderUntilTheGapIsFilled()
    {
        Receive(2, "c");
        Receive(1, "b");

        Assert.That(delivered, Is.Empty);
        Assert.That(receiver.CommandsAheadOfOrderCount, Is.EqualTo(2));

        Receive(0, "a");

        Assert.That(delivered, Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(receiver.NextExpectedIndex, Is.EqualTo(3));
        Assert.That(receiver.CommandsAheadOfOrderCount, Is.Zero);
    }

    [Test]
    public void ResetStartsAtTheGivenIndexAndDropsHeldCommands()
    {
        Receive(5, "held");
        receiver.Reset(10);

        Receive(9, "old");
        Receive(10, "new");

        Assert.That(delivered, Is.EqualTo(new[] { "new" }));
        Assert.That(receiver.CommandsAheadOfOrderCount, Is.Zero);
    }

    [Test]
    public void LimitsHowManyCommandsAreHeldAheadOfOrder()
    {
        for (int index = 1; index <= InOrderCommandReceiver<string>.MaxCommandsAheadOfOrder + 10; index++)
            Receive(index, $"cmd{index}");

        Assert.That(receiver.CommandsAheadOfOrderCount, Is.EqualTo(InOrderCommandReceiver<string>.MaxCommandsAheadOfOrder));
    }
}

[TestFixture]
public class UnacknowledgedCommandWindowTest
{
    private static byte[] PayloadOfLength(int length) => new byte[length];

    private static List<int> IndicesOf(List<RedundantCommand> commands) => commands.Select(command => command.index).ToList();

    [Test]
    public void SelectsOnlyCommandsAtOrAfterTheFirstNeededIndex()
    {
        var window = new UnacknowledgedCommandWindow();
        window.Add(0, [1]);
        window.Add(1, [2]);
        window.Add(2, [3]);

        Assert.That(IndicesOf(window.SelectForPacket(1)), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public void ForgetsAcknowledgedCommands()
    {
        var window = new UnacknowledgedCommandWindow();
        window.Add(0, [1]);
        window.Add(1, [2]);
        window.Add(2, [3]);

        window.ForgetCommandsBefore(2);

        Assert.That(window.Count, Is.EqualTo(1));
        Assert.That(IndicesOf(window.SelectForPacket(0)), Is.EqualTo(new[] { 2 }));
    }

    [Test]
    public void StaysWithinTheByteBudgetAndSkipsCommandsThatDontFit()
    {
        int budget = UnacknowledgedCommandWindow.MaxRedundantBytesPerPacket;
        int overhead = RedundantCommand.EncodingOverheadBytes;

        var window = new UnacknowledgedCommandWindow();
        window.Add(0, PayloadOfLength(budget / 2 - overhead));
        window.Add(1, PayloadOfLength(budget));
        window.Add(2, PayloadOfLength(budget / 2 - overhead));
        window.Add(3, PayloadOfLength(1));

        var selected = window.SelectForPacket(0);

        Assert.That(IndicesOf(selected), Is.EqualTo(new[] { 0, 2 }));
        Assert.That(selected.Sum(command => command.EncodedSize), Is.LessThanOrEqualTo(budget));
    }

    [Test]
    public void DropsTheOldestCommandsBeyondTheRetentionLimit()
    {
        var window = new UnacknowledgedCommandWindow();
        for (int index = 0; index < UnacknowledgedCommandWindow.MaxRetainedCommands + 5; index++)
            window.Add(index, []);

        Assert.That(window.Count, Is.EqualTo(UnacknowledgedCommandWindow.MaxRetainedCommands));
        Assert.That(window.SelectForPacket(0).First().index, Is.EqualTo(5));
    }
}

[TestFixture]
public class ServerCommandRedundancyTest
{
    private const int MapId = 1;

    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private ServerPlayer AddPlayingPlayer(string username)
    {
        var conn = new RecordingConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);
        server.playerManager.Players.Add(player);
        return player;
    }

    private static ClientCommandPacket DesignatorCommand(int index, byte marker) =>
        new(CommandType.Designator, MapId, [marker]) { index = index };

    private static RedundantCommand Redundant(ClientCommandPacket packet) => new(packet.index, packet.ToPayload());

    private List<byte> ExecutedMarkers() =>
        server.worldData.mapCmds[MapId]
            .Select(serialized => ScheduledCommand.Deserialize(new ByteReader(serialized)).data[0])
            .ToList();

    [Test]
    public void ExecutesClientCommandsOnceAndInOrderWhicheverCopyArrivesFirst()
    {
        var player = AddPlayingPlayer("player");
        var state = player.conn.GetState<ServerPlayingState>()!;

        state.HandleRedundantCommands(new ClientRedundantCommandsPacket(
            [Redundant(DesignatorCommand(1, 11)), Redundant(DesignatorCommand(2, 12))]));

        Assert.That(server.commands.SentCmds, Is.Zero);

        state.HandleClientCommand(DesignatorCommand(0, 10));
        state.HandleClientCommand(DesignatorCommand(1, 11));
        state.HandleClientCommand(DesignatorCommand(2, 12));
        state.HandleRedundantCommands(new ClientRedundantCommandsPacket(
            [Redundant(DesignatorCommand(0, 10)), Redundant(DesignatorCommand(2, 12))]));

        Assert.That(server.commands.SentCmds, Is.EqualTo(3));
        Assert.That(ExecutedMarkers(), Is.EqualTo(new byte[] { 10, 11, 12 }));
        Assert.That(player.clientCommands.NextExpectedIndex, Is.EqualTo(3));
    }

    [Test]
    public void KeepsServerCommandsUntilEveryPlayingPlayerAcknowledgedThem()
    {
        var fastPlayer = AddPlayingPlayer("fast");
        var slowPlayer = AddPlayingPlayer("slow");

        for (int i = 0; i < 3; i++)
            server.commands.Send(CommandType.Designator, 0, MapId, [(byte)i]);

        fastPlayer.AcknowledgeServerCommands(3);
        slowPlayer.AcknowledgeServerCommands(1);
        server.commands.ForgetCommandsAcknowledgedByAllPlayingPlayers();

        var forSlowPlayer = server.commands.recentCommands.SelectForPacket(slowPlayer.acknowledgedServerCommands);
        var forFastPlayer = server.commands.recentCommands.SelectForPacket(fastPlayer.acknowledgedServerCommands);

        Assert.That(forSlowPlayer.Select(command => command.index), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(forFastPlayer, Is.Empty);
    }

    [Test]
    public void AcknowledgementsNeverMoveBackwards()
    {
        var player = AddPlayingPlayer("player");

        player.AcknowledgeServerCommands(5);
        player.AcknowledgeServerCommands(2);

        Assert.That(player.acknowledgedServerCommands, Is.EqualTo(5));
    }

    [Test]
    public void RedundantServerCommandsDeserializeToTheSentCommand()
    {
        AddPlayingPlayer("player");
        server.commands.Send(CommandType.Designator, 3, MapId, [42]);

        var redundant = server.commands.recentCommands.SelectForPacket(0).Single();
        var cmd = ScheduledCommand.Deserialize(new ByteReader(redundant.payload));

        Assert.That(redundant.index, Is.Zero);
        Assert.That(cmd.type, Is.EqualTo(CommandType.Designator));
        Assert.That(cmd.factionId, Is.EqualTo(3));
        Assert.That(cmd.mapId, Is.EqualTo(MapId));
        Assert.That(cmd.data, Is.EqualTo(new byte[] { 42 }));
    }
}
