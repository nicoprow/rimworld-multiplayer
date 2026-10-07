# Netcode Tasks

This is the working file. It always shows the current state: overwrite outdated information and do not keep a history (git has that). Background and sources are in [netcode-research.md](netcode-research.md).

Status values: `todo`, `research`, `in progress`, `done`, `dropped`.

---

## 1. Testing setup and metrics

**Status:** done

**Goal:** a repeatable bad-network setup (reference: 500 ms ±100 ms jitter, 5% loss), plus measurements of every later change against it.

**Done:**
- Dev setup: the repo is linked into the RimWorld `Mods` folder (directory junction), and the Workshop version is disabled.
- `Source/NetworkConditioner`: a UDP relay console app. Per direction it applies:
  - delay
  - uniform jitter, with order preserved unless `--reorder` is given
  - random loss
  - time-based loss bursts

  It opens one socket per client towards the host and takes a fixed `--seed`. `--help` lists the options. The decision logic (`ImpairedLink`) is unit tested in `Source/Tests/NetworkConditionerTest.cs`.
- `scripts/Start-NetcodeTest.ps1` starts the relay, a host instance and a client instance. The client gets its own data folder; the mod list is copied there on first run. The client auto-joins through the relay.
  - The default is the reference profile: 250 ms ±50 ms and 2.5% loss per direction.
  - `-HostSave <name or path>` hosts a multiplayer save (`.zip` in `MpReplays`) automatically. It passes `-mphostreplay` and the new `-mphostauto` flag, which runs the Host button straight away using the host settings saved in the mod (port, LAN, Steam). If hosting fails, the host window stays open. Singleplayer `.rws` saves aren't accepted; host them once and save them as a multiplayer save first.
  - The client is only started once the host is listening on its port (checked with `Get-NetUDPEndpoint`), because the client's auto-join doesn't retry. `-ClientExtraDelaySeconds` adds a delay on top, and `-HostStartTimeoutSeconds` (default 300) sets how long to wait.
  - The host window is placed on the left half of the primary screen and the client on the right, both windowed. The client's `Prefs.xml` gets the matching screen size, because RimWorld resizes the window from it while loading. `-NoWindowLayout` turns this off.
  - Options: `-NoAutoConnect`, `-NoRelay`, `-NoHostInstance`, `-NoClientInstance`, `-NoWindowLayout` and the relay parameters.
  - Test colony: `New Arrivals1` (5 MB singleplayer save). Use the same save for every baseline so numbers stay comparable.
- `NetworkMetrics` (`Source/Client/UI/DebugPanel`) measures on each client:
  - buffer depth (`tickUntil - Timer`) at the start of each frame
  - stalls: count and duration of stretches where `Timer >= tickUntil`. Simulation catch-up, replays, server freezes and long events are excluded.
  - timer ticks run per frame
  - command round trip: from sending your own command until the server's copy arrives. It matches on type, map and data, so commands the server rejects don't throw the matching off.
  - command latency: from sending until the command executes locally
- `PerformanceRecorder` writes these metrics to a "NETWORK CONDITIONS" section of `MpPerf-*.txt`, with P95 and P99 for every metric. `SyncDebugPanel` shows them live in a "NETWORK CONDITIONS" section.

**Baseline** (client side, `New Arrivals1`, mixed game speeds, mod at `70ce34c`):

| Metric | Reference relay (44 s) | No relay (28 s) |
|---|---|---|
| Buffer depth, ticks (avg / P95 / max) | 7.2 / 33 / 52 | 3.3 / 5 / 11 |
| Stalls | 15, 2.56 s total, 5.8% of the time | 0 |
| Stall duration, ms (avg / max) | 170 / 818 | – |
| Command round trip, ms (min / avg / max) | 503 / 1176 / 2065 | 22 / 44 / 98 |
| Command latency to execution, ms (min / avg / max) | 599 / 1345 / 2087 | 113 / 133 / 232 |
| Ticks per frame (avg) | 0.90 | 0.94 |
| TPS performance (avg) | 92% | 95% |

Both runs are short, with fewer than 30 command samples each, so the P95 and P99 values are not reliable yet. Use runs of several minutes when comparing later changes.

**Findings:**
- **Command round trip under loss is far above the configured delay.** The minimum (503 ms) matches 2 × 250 ms, but the average is 2.3 times that and the maximum about 4 times. The cause is LiteNetLib's resend delay of about 1.1 s at this RTT, together with all reliable traffic sharing one ordered stream (see task 2).
- **Executing a command takes about 90 ms (5 ticks) longer than its round trip without the relay, and about 170 ms with it.** That is the time a command waits in the tick buffer.
- **Clients run at most one timer tick per frame.** Outside replays and simulation, `TickPatch.Prefix` sets `ticksToRun = 1`. The 0.8× "speed up" therefore can't exceed the frame rate, and a client that has fallen behind catches up slowly instead of in bursts. Relevant for tasks 3 and 4.
- **LiteNetLib simulation doesn't work in our build.**
  - In 1.3.1, `SimulatePacketLoss`, `SimulateLatency` and the related fields are public and can be set.
  - However, the methods that apply them (`HandleSimulateLatency`, `HandleSimulatePacketLoss`, `ProcessDelayedPackets`) are marked `[Conditional("DEBUG")]`. The NuGet DLL is a release build, so the compiler removed every call to them, which I confirmed by scanning the IL. Setting the flags has no effect and gives no error.
  - The simulation would also only act on received packets, and its latency is a uniform random value between min and max.
- **`PacketLayerBase` (an optional constructor argument of `NetManager`) is not enough on its own.** It can drop packets: an inbound length of 0 discards the packet. It can't delay them, because processing is synchronous and there's no way to put a packet back in later.
- **Loss can't be simulated above LiteNetLib.** Dropping messages in `LiteNetConnection` after the reliable layer has acknowledged them loses data instead of triggering a resend. It has to happen at the UDP level, so the plan is the relay.
- **clumsy** (WinDivert) only works for rough checks. Its lag is fixed per packet with no jitter (only reordering), and it is deliberately imprecise. On loopback each packet is processed twice, so a 500 ms setting gives about 1000 ms.
- **Running host and client on one machine:**
  - The mod already starts a second RimWorld process for the Arbiter (`HostUtil.StartArbiter`), so several instances can run at the same time.
  - Useful command-line arguments for a second instance:
    - `-connect=127.0.0.1:<port>`: joins automatically on startup (`AutoJoinHandler`)
    - `-username=<name>`
    - `-savedatafolder=<dir>`: separate config and saves, so the two instances don't share mod settings
    - `-mphostreplay=<file>`: opens hosting for a save automatically
  - Tested: two windowed instances run side by side, and both keep simulating when not focused. In Windows PowerShell 5.1, quote the arguments (`"-connect=127.0.0.1:30502"`). Unquoted, PowerShell splits `-connect=127.0.0.1` at the first dot and the auto-join fails.
  - `Source/Server` is a headless .NET 8 standalone server. It reads `settings.toml` and `save.zip`, and supports neither Steam nor the Arbiter. It can be the host so both game instances are equal clients, but it adds bootstrap steps. Host in-game first.
  - `Source/Tests/ServerTest.cs` connects LiteNetLib clients to an in-process server. This is useful for automated tests of the packet layer without RimWorld.
- **Steam:** you can't test this on one machine. Two instances share one Steam account and SteamID. It needs a second machine or VM with a second Steam account, plus clumsy on one side. clumsy is enough here because the Steam path is secondary.
- **Debug UI:**
  - `SyncDebugPanel` (`Source/Client/UI/DebugPanel`) is the live overlay, built from collapsible `DebugSection`s. It only appears in a Debug build of the mod (`MpVersion.IsDebug`) with dev mode on and "Show debug info" enabled in the mod settings.
  - `PerformanceRecorder` is called every frame from `OnMainThread`, and writes `MpPerf-*.txt` to `Multiplayer.LogsDir`.
  - `dotnet build` defaults to Debug and copies the output to the repo root (`ModOutputPath`), which is the mod folder.

---

## 2. Separate channels and send commands redundantly

**Status:** in progress

**Goal:** a single lost packet no longer stalls the command stream.

**Priority:** Steam is the main way the game is played. Prefer fixes that work on both transports over LiteNetLib-only ones.

**Done** (protocol 55):
- `ClientFrameTimePacket` is sent unreliably.
- Redundant commands, in both directions:
  - Every command has a sequence index: the server's `CommandHandler.SentCmds` before the send, and a per-session counter on the client (`MultiplayerSession.SendOwnCommand`). The index is in `ServerCommandPacket.index` and `ClientCommandPacket.index`.
  - Until acknowledged, each command is repeated in every unreliable packet sent at the net tick rate. Server to client: inside `ServerTimeControlPacket.redundantCommands`, acknowledged through `ClientKeepAlivePacket.receivedCommands`. Client to server: in `ClientRedundantCommandsPacket` from `MultiplayerSession.Update` every 33 ms, acknowledged through `ServerTimeControlPacket.acknowledgedClientCommands`.
  - The reliable command packets are still sent. Commands that don't fit the 1000-byte budget (`UnacknowledgedCommandWindow.MaxRedundantBytesPerPacket`) arrive that way. The budget keeps every packet below Steam's roughly 1200-byte unreliable limit and LiteNetLib's starting MTU of 1164.
  - `InOrderCommandReceiver` delivers commands strictly in index order and drops duplicates. On the client it replaces `receivedCmds`, which is now `serverCommands.NextExpectedIndex`. On the server there is one per player (`ServerPlayer.clientCommands`).
  - The server keeps sent commands in `CommandHandler.recentCommands` until every playing player has acknowledged them, but at most 4096. A player's acknowledgement starts at `SentCmds` when they connect and when their world data is sent.
  - Tests are in `Source/Tests/CommandRedundancyTest.cs`.

**Result with the reference relay** (68 s, 40 commands, compared with the task 1 baseline):

| Metric | Baseline | Redundant commands |
|---|---|---|
| Command round trip, ms (min / avg / P95 / max) | 503 / 1176 / 2065 / 2065 | 484 / 603 / 734 / 1331 |
| Command latency to execution, ms (avg / max) | 1345 / 2087 | 703 / 1347 |
| Stalls | 15, 5.8% of the time, longest 818 ms | 12, 1.2% of the time, longest 568 ms |
| Buffer depth, ticks (avg / P95) | 7.2 / 33 | 3.9 / 7 |

No lost, doubled or misordered commands, and no desync. Playing felt much smoother. What remains is the delay before the game shows that an order was accepted. That is the round trip itself, which lockstep can't avoid; task 5 (latency hiding) addresses it.

**Remaining work:**
- Find the cause of the remaining outliers (round trip up to 1331 ms, one 568 ms stall). The host's log of that run was lost; the script now writes separate logs (`Player-Host.log` in the main data folder, `Player.log` in the client data folder).
- Record a real Steam session before and after (both players need the same build).
- Measure how big commands and sync opinions are in practice.
- If large commands turn out to be common, raise the redundancy budget or split a command across several redundant packets.
- If late unfreezes become noticeable, put the freeze state into `ServerTimeControlPacket`. `ServerFreezePacket` is reliable, so a lost one unfreezes clients about a second late.
- Raise disconnect timeouts so they survive several seconds of loss bursts (LiteNetLib `DisconnectTimeout` is the default 5000 ms).

**Decision: no separate channels for now.**
- Commands, time control and frame times no longer depend on the reliable stream, so channels wouldn't change how the game feels.
- What would still benefit:
  - commands too large for the redundancy budget
  - freeze and unfreeze
  - chat, selections, pings and the player list
- Channels don't raise throughput. The window of 64 packets and the resend delay apply to each channel separately, so the slow world download would stay slow.
- Cost: fragment state per channel, another protocol bump, and a separate solution for Steam.
- The targeted fixes for large commands and freeze delays are under remaining work.

**Findings:**
- **One ordered stream carries all reliable traffic.** `ConnectionBase.Send` takes only a `reliable` flag. `LiteNetConnection.SendRaw` maps it to `ReliableOrdered` or `Unreliable`, always on channel 0, and the server's `NetManager`s use the default `ChannelsCount` of 1. A lost packet therefore holds back every reliable packet sent after it, whatever its type.
- **Heavy periodic traffic shares that stream with commands:**
  - Client to server: `ClientFrameTimePacket` is sent reliable about 30 times a second (`TickPatch`, every 32 ms). With 2.5% loss, roughly one of these is lost every 1.3 seconds and blocks the client's own commands behind it.
  - Server to client: every 30 ticks the host's sync opinion is forwarded to each other client as a reliable packet fragmented by our own code (1 KB parts, `ServerPlayingState.HandleDesyncCheck`). Also reliable: player latencies once a second, selections, pings, chat, freeze, player list.
  - Unreliable today: time control (`Server_TimeControl`), keep-alives in both directions, cursors.
- **LiteNetLib resends late on slow links.** In 1.3.1 the resend delay is `25 ms + 2.1 × average RTT` (`NetPeer.UpdateRoundTripTime`). At 500 ms RTT that is about 1.1 s before the first resend, plus another half RTT for the resent packet to arrive. A lost ping counts as an RTT sample of about 1000 ms (the ping interval), which pushes the delay higher. This matches the baseline: the shortest round trip was 503 ms, the average 1176 ms and the longest 2065 ms (a packet lost twice).
- **The window can fill up.** Each reliable channel allows 64 packets that haven't been acknowledged yet. While a lost packet waits for its resend (about 1.6–2 s at 500 ms RTT), the 30 frame-time packets a second alone use 48–60 of those 64 slots. Anything beyond waits in the send queue.
- **The client's tick gate is `sentCmdsSnapshot`.** The server stamps each command with its current `gameTimer`, and with each timer increase it stores `sentCmdsSnapshot = commands.SentCmds`. The client takes the `tickUntil` from `ServerTimeControlPacket` only if `receivedCmds >= remoteSentCmds` (`MultiplayerSession.ProcessTimeControl`). A server command stuck behind a lost packet freezes `tickUntil` until it arrives, which is the stall in the baseline. A client command stuck on the way to the server only delays that command.
- **LiteNetLib channels** (1.3.1):
  - Each pair of (channel number, delivery method) is its own independent stream, and the channel index is `channelNumber × 4 + deliveryMethod`. `Unreliable` has no channel. So `ReliableOrdered` and `ReliableUnordered` on channel 0 already don't block each other.
  - `ChannelsCount` (1–64) must be equal on both sides. Packets for a channel number the receiver doesn't have are dropped without an error.
  - `Sequenced` delivers only the newest packet and drops older ones. `ReliableSequenced` resends only the last packet.
  - Only `ReliableOrdered` and `ReliableUnordered` can be fragmented. A larger `Unreliable` or `Sequenced` packet throws `TooBigPacketException`. The MTU starts at 1164 bytes (1232 − 68) and is raised by MTU discovery up to 1432. The header for unreliable packets is 1 byte.
  - `DisconnectTimeout` is the default 5000 ms; we don't set it.
- **Our own fragmentation allows only one fragmented packet at a time per connection** (`ConnectionBase.MaxFragmentedPackets = 1`, more throws `PacketReadException`). This works today only because everything arrives in one ordered stream. With more than one channel, two fragmented packets (for example world data and a sync opinion) would interleave, so fragment state has to be kept per channel.
- **Steam** uses the old `SteamNetworking` P2P API. It already uses the channel number to tell connections apart (each client picks a random receive channel), sends reliable or unreliable, and allows about 1200 bytes per unreliable packet. Separate reliable channels would need more channel numbers per connection. Redundant commands in unreliable packets don't depend on channels, so they help Steam too.
  - On Steam, everything above our own code is different: LiteNetLib isn't involved, so its resend timing doesn't apply. Valve doesn't document how the old P2P API times its resends. The newer `ISteamNetworkingSockets` uses an ack scheme modelled on QUIC, but it isn't confirmed that the old API runs on top of it.
  - Our own code behaves the same on both: one ordered reliable stream per connection, frame time sent reliably, sync opinions fragmented, and the same gate on the client. Head-of-line blocking therefore exists on Steam too. How long each blockage lasts is unknown.
  - The relay can't sit between two Steam peers. `NetworkMetrics` measures in the game rather than in the transport, so recordings from real Steam sessions are comparable with the relay baseline.
- **Protocol:** `MpVersion.Protocol` is 54. Any of these changes needs a bump. Client and server must have the same version, so old clients can't connect.
- **The average round trip is about 100 ms above the configured 500 ms.** The relay keeps packets in order, so a packet can't overtake one with a longer jitter delay. With 30 packets a second, the delay tends towards the upper end of the jitter range (about 300 ms instead of 250 ms per direction). Real links with jitter queue packets in order too. The rest comes from tick rates: the server polls the network at 30 Hz, the client once per frame, and LiteNetLib sends every 15 ms.
- **The world download for a join is slow under loss.** 839 KB took 22 s through the reference relay (about 37 KB/s), because it is sent as reliable 1 KB fragments in one ordered window of 64 packets. Relevant for task 7.
- **Joining and rejoining:** the world data carries `SentCmds`, and the client starts its command receiver there (`ClientLoadingState`). The server switches the player to playing in the same step as sending the world data, so no command falls in between. A rejoin keeps the same connection and `ServerPlayer`, so the client's own command counter keeps counting.

---

## 3. Buffer sized from jitter, with smooth speed adjustment

**Status:** todo

**Goal:** a steady tick rate with no visible speed-ups or slow-downs, and enough buffer to ride out jitter.

**Work:**
- Measure how irregularly time-control packets arrive on the client. Turn that into a target buffer depth (a high percentile plus a margin).
- Replace the fixed 3 and 7 tick thresholds with a continuous speed controller (roughly ±2–10%) that steers towards the target.
- Raise the buffer target quickly when jitter or loss increases, and lower it slowly.
- Spread ticks evenly across frames instead of bursts.

**Research needed:**
- `TickPatch.Prefix` / `DoUpdate`: how `serverTimePerTick`, `realTime` and `ticksToRun` interact, and how game speed settings (TimeSpeed multipliers) change ticks per frame.
- How this interacts with `ClientFrameTimePacket` and the server averaging frame time across players.
- How controllers are built in the reference material: Overwatch time dilation, AoE "rise fast, settle slowly", standard jitter-buffer estimators (e.g. RFC 3550 interarrival jitter).

---

## 4. Stop lagging players from slowing the server

**Status:** todo

**Goal:** one bad connection no longer pauses or slows the game for everyone.

**Work:**
- Rework the main server loop: drop or loosen the slowdown based on the worst player's `ticksBehind`, and raise the threshold of 90 ticks behind for pausing.
- A lagging player catches up on their own using the existing simulation budget per frame.
- Show in the UI which player is lagging, and by how much.
- Keep a hard limit for players who fall too far behind (pause or suggest a rejoin).

**Research needed:**
- Why the current slowdown and pause exist. Check git history and issues for the intent (fairness? memory for buffered commands? joining players?).
- How `ExtrapolatedTicksBehind` and `ticksBehind` are calculated, and how `frameTime` from slow PCs feeds into `serverTimePerTick`.
- Whether gameplay requires players to stay close in time (e.g. trading, shared maps, the vote/pause systems).

---

## 5. Latency hiding for common orders

**Status:** in progress

**Goal:** a player's own orders appear instantly, even at 500 ms ping.

**Done:**
- `PendingOrderRegistry` (`Source/Client/UI/PendingOrders`) keeps one `PendingOrderOverlay` per own command that hasn't run yet.
  - Before sending, the UI code calls `AttachToNextOwnCommand(overlay)`. `Extensions.SendCommand` hands the overlay to the next own command and records its type, map and data (`OwnCommandSignature`, shared with `NetworkMetrics`).
  - `TickPatch.RunCmds` removes the overlay when the matching own command runs. That is the same frame in which the real designation or blueprint appears. Overlays also expire 10 s after sending, which covers commands the server rejects.
  - Overlays are drawn in a postfix on `MapInterface.MapInterfaceUpdate`, only for the current map and not while simulating. They only read game state.
- Overlays so far:
  - designators with a `Designation` def (mine, cut, harvest, hunt, deconstruct, ...): the faded designation icon at the cells or on the thing
  - `Designator_Place` (build, install): a blue ghost of the thing with the chosen rotation and stuff
  - all other cell designators (zones, areas, plans, cancel): the cell outline
  - drafted moves (`FloatMenuOptionProvider_DraftedMove.PawnGotoAction`): a line from the pawn to the destination and a target highlight

**Remaining work:**
- Test in game with the relay: check every overlay type, and that nothing stays behind after its command ran.
- Toggles that read game state directly still show the old state until the command runs: drafting, forbidding, and checkboxes in windows.
  - Unbuffered sync fields send immediately and restore the field at once, so the checkbox flips back until the command arrives. These could get the same pending display as buffered fields, without the 200 ms delay.
  - Sync methods such as `Pawn_DraftController.Drafted` would need a UI-only override of the getter, which is riskier. Check which ones matter most.
- Edge case: orders that depend on a pending one (for example a move right after drafting). The server keeps the per-player order, so they run correctly; only the display may be briefly wrong.

**Findings:**
- **Commands are sent from a few places.** Designators go through `DesignatorPatches` (`CommandType.Designator`). Sync methods, sync fields and sync delegates go through `SyncHandler.SendSyncCommand` (`CommandType.Sync`). Everything ends in `Extensions.SendCommand`, which is the central place to attach pending state.
- **Buffered sync fields already hide latency.** `SyncFieldUtil` keeps the locally changed value of fields with `SetBufferChanges()` and shows it until the server's command has applied it. It sends the change after 200 ms without further edits. Unbuffered fields restore the old value straight away and show the new one only once the command has run.
- **Drawing APIs** (1.6): `Designator.Designation` (a `DesignationDef` with `iconMat`), `FadedMaterialPool.FadedVersionOf`, `GhostDrawer.DrawGhostThing`, `GenDraw.DrawFieldEdges`, `GenDraw.DrawLineBetween` and `GenDraw.DrawTargetHighlight`. Assembly-CSharp is publicized, so protected members such as `Designation` are accessible.
- **Overlays draw meshes directly instead of creating flecks or motes.** Those live in the map's managers, and their lifetime can't be tied to a pending command. The existing goto fleck appears only once the own command runs.

---

## 6. Commands targeting a future tick

**Status:** todo

**Goal:** predictable delay, the same for every player, instead of one that depends on when commands arrive.

**Work:**
- The client asks for an execution tick: estimated server tick plus the delay. The server clamps it into a valid window and keeps ordering deterministic.

**Research needed:**
- Whether this is still worth it once tasks 2, 3 and 5 are done. Decide after measuring.
- Estimating server time on the client (clock offset and RTT estimation).
- Effect on replays and on commands sent by the host or server.

---

## 7. Rejoin in the background

**Status:** todo

**Goal:** a desync or rejoin no longer freezes the host or the other players, and the affected player gets clear progress.

**Work:**
- The host keeps a recent cached snapshot plus the commands since then, so it doesn't freeze to serialise on demand.
- Stream the snapshot in chunks on the bulk channel.
- The desynced client shows progress. After loading, it catches up with a time budget per frame and moves smoothly back into normal play.

**Research needed:**
- The current rejoin and join flow: `Rejoiner`, `ServerJoiningState`, `ServerLoadingState`, the bootstrap packets, and how and when the host creates the save (`SaveLoad`, world data packets).
- How long serialising takes and how big saves get on large colonies.
- Whether the host's autosaves or replay data can serve as the cached snapshot.
- How `simulating` / catch-up mode in `TickPatch` works today.

---

## 8. Resync one map at a time

**Status:** research

**Goal:** after a desync, reload only the affected map plus world state, not the whole game.

**Work:**
- Hash each map and world state separately, so the divergent part is identified.
- Serialise and reload one map while the rest of the game keeps running.

**Research needed:**
- Whether this is possible at all: cross-map references (caravans, pawns moving between maps, world objects, factions, the shared ID counter), and RimWorld loading one map on its own.
- How async time per map (`AsyncTimeComp`) separates map state today.
- How desync detection currently hashes state (`Client/Desyncs`, opinions and traces). Can it be split per map?
- What other RimWorld multiplayer forks or projects have tried here.
