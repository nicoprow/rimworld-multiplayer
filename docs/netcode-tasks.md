# Netcode Tasks

This is the working file. It always shows the current state: overwrite outdated information and do not keep a history (git has that). Background and sources are in [netcode-research.md](netcode-research.md).

Status values: `todo`, `research`, `in progress`, `done`, `dropped`.

---

## 1. Testing setup and metrics

**Status:** todo

**Goal:** a repeatable bad-network setup (reference: 500 ms ±100 ms jitter, 5% loss), plus measurements of every later change against it.

**Work:**
- Dev setup: link the repo into the RimWorld `Mods` folder (directory junction), and disable the Workshop version of the mod if it's subscribed.
- Write a small UDP relay as a dev tool, e.g. a console project `Source/NetworkConditioner`. The client connects to the relay, which forwards to the server and applies per-packet delay with jitter, loss (random and bursty) and a fixed seed, separately for each direction.
- Collect metrics on each client:
  - command buffer depth over time (`tickUntil - Timer`). `PerformanceRecorder` already samples this as "timer lag" and reports average, min and max.
  - stall count and duration (time spent with `Timer >= tickUntil`)
  - ticks run per frame, to show catch-up bursts
  - how long it takes from issuing a command until it runs locally
- Add the new metrics to `PerformanceRecorder` (file output) and a new section in `SyncDebugPanel` (live view). Add percentiles (p95, p99), since averages hide stalls.

**Findings:**
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

**Status:** todo

**Goal:** a single lost packet no longer stalls the command stream.

**Work:**
- Use separate channels for:
  - the real-time stream (commands, time control)
  - bulk data (saves, join data, traces, desync reports)
  - chat and UI state (cursor, selections, pings)
- Command stream: each packet carries every command not yet acknowledged, tagged with a sequence number. The receiver removes duplicates and acknowledges the highest number it has received in order. The sender drops acknowledged commands.
- Keep reliable delivery for bulk data, but on its own channel.
- Raise disconnect timeouts so they survive several seconds of loss bursts.

**Research needed:**
- The full packet pipeline: `ConnectionBase`, `Packets.cs`, `FragmentedPacket`, and how `reliable` is chosen per packet type.
- How the client currently makes sure it has every command for a tick before simulating it. Is `sentCmdsSnapshot` in `ServerTimeControlPacket` the gate?
- LiteNetLib 1.3.1 channel API (`ChannelsCount`, channel number when sending), `ReliableUnordered` / `Sequenced` behaviour, MTU and fragmentation limits.
- Steam: what the old `SteamNetworking` API can do (channels, send types), versus moving to `SteamNetworkingSockets`. Steam may need a separate task.
- Largest realistic size of a single command, which determines the redundancy budget per packet.
- Effect on protocol version and compatibility (`MpVersion`, protocol packet).

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

**Status:** todo

**Goal:** a player's own orders appear instantly, even at 500 ms ping.

**Work:**
- A speculative display layer drawn on top of the real game state, never mixed into it:
  - designations and blueprints
  - move targets for drafted pawns
  - zone and area painting
  - changes to bills and settings in open windows
- Remove each speculative entry when its confirmed command runs, or when it times out.

**Research needed:**
- How sync methods and fields send commands today (`SyncHandlers`, `Sync` attributes, the MultiplayerAPI). Find a central place to record pending local commands.
- How RimWorld draws designations, blueprints, zones and pawn paths, so speculative versions can use the same drawing code without touching game state.
- Which actions are frequent enough to matter. Start with designations and draft moves.
- How the mod already treats UI-only state that is never synced.
- Factorio FFF #83 / #302 for edge cases (ordering, actions that depend on each other).

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
