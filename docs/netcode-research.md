# Netcode Research: Resilience to Packet Loss, High Ping and Resyncs

Goal: keep gameplay smooth at up to 500 ms ping, with packet loss and jitter, and make desync recovery less disruptive.

## How the mod works today

- **Model:** host-authoritative deterministic lockstep. Every client runs the full simulation and only commands go over the network.
- **When commands run:** the server stamps each incoming command with its current `gameTimer` and sends it to everyone as reliable and ordered ([CommandHandler.cs](../Source/Common/CommandHandler.cs), [LiteNetConnection.cs](../Source/Common/Networking/LiteNetConnection.cs)). So the player who issued it sees the effect only after a full round trip plus their tick buffer.
- **How far clients may run:** the server sends `ServerTimeControlPacket` unreliably about 30 times a second. It tells clients the latest tick they may simulate up to (`tickUntil`) ([MultiplayerServer.cs](../Source/Common/MultiplayerServer.cs), `TickNet`).
- **Client pacing:** a client runs at 1.2× tick time (slower) when 3 or fewer ticks are buffered, and 0.8× (faster) at 7 or more ([TickPatch.cs](../Source/Client/Patches/TickPatch.cs), `Prefix`). It's a simple step function.
- **Server pacing:** the server pauses everyone if any player is more than 90 ticks behind. It also slows the whole game, up to 3× slower, based on the worst player's `ticksBehind` ([MultiplayerServer.cs](../Source/Common/MultiplayerServer.cs), main loop).
- **Desync recovery:** a full rejoin, meaning a fresh save download, a reload and a catch-up simulation ([DesyncedWindow.cs](../Source/Client/Windows/DesyncedWindow.cs), `Rejoiner.DoRejoin`).
- **Transport:** both connection types are UDP.
  - Direct IP uses LiteNetLib 1.3.1, with `ReliableOrdered` or `Unreliable` delivery.
  - Steam uses the old `SteamNetworking.SendP2PPacket` API, with `k_EP2PSendReliable` or `k_EP2PSendUnreliable` ([NetworkingSteam.cs](../Source/Client/Networking/NetworkingSteam.cs)). This API is deprecated in favour of `SteamNetworkingSockets`.

## Main sources

| Source | Why it matters |
|---|---|
| Bettner & Terrano, [*1500 Archers on a 28.8*](https://www.gamedeveloper.com/programming/1500-archers-on-a-28-8-network-programming-in-age-of-empires-and-beyond) (Age of Empires, GDC 2001) | The standard lockstep RTS paper. Commands are scheduled two turns ahead, and turn length adapts to ping and frame rate. |
| Glenn Fiedler, [*Deterministic Lockstep*](https://gafferongames.com/post/deterministic_lockstep/) | Shows why TCP-style reliable delivery stalls lockstep, and how redundant sends over UDP plus a playout buffer fix it. |
| Factorio FFF [#76](https://factorio.com/blog/post/fff-76), [#83 *Hide the latency*](https://factorio.com/blog/post/fff-83), [#302 *Multiplayer megapacket*](https://factorio.com/blog/post/fff-302), [#188 desyncs](https://factorio.com/blog/post/fff-188) | The closest match to RimWorld: a huge deterministic sim, a server that relays inputs, latency hiding, and desync recovery by re-download. |
| Forrest Smith, [*Synchronous RTS Engines and a Tale of Desyncs*](https://www.forrestthewoods.com/blog/synchronous_rts_engines_and_a_tale_of_desyncs/) and [part 2](https://www.forrestthewoods.com/blog/synchronous_rts_engines_2_sync_harder/) (Supreme Commander) | Desync detection, and the limits of recovering from a desync. |
| Tim Ford, [*Overwatch Gameplay Architecture and Netcode*](https://gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and) (GDC 2017) | Adjusts input buffer size against packet loss by subtly changing simulation speed. |

## What to keep, and what not to adopt

- **Keep lockstep.** AoE dropped state syncing because even about 250 units saturated the link. A RimWorld colony is far larger.
- **Skip rollback (GGPO) and full client prediction.** They need cheap snapshot and re-simulation. RimWorld ticks are expensive and its state can't realistically be rewound.
- **Favour steady latency over low latency.** AoE found under 250 ms imperceptible, 250–500 ms very playable, and that steady lag beat variable lag. RimWorld is slower-paced than AoE, so a stable 500 ms is a reachable target if it stays stable.

## Techniques by problem

### 1. Packet loss

- **Head-of-line blocking is the main problem.** All commands share one reliable, ordered stream. One lost packet holds back every later command until it is resent, which takes at least one round trip plus the retransmit timeout. Clients then run out of commands for upcoming ticks and freeze.
  - Fiedler measured stutters with TCP-style delivery at 100 ms / 1% loss, and severe stalls at 250 ms / 5%. With redundancy it stayed smooth at 2 s latency and 25% loss.
- **Send redundantly.** Each outgoing packet repeats every command that hasn't been acknowledged yet, tagged with its tick and sequence number. The receiver removes duplicates. Commands are small, so the bandwidth cost is low. AoE did something similar by resending aggressively on the assumption that packets were lost.
- **Separate traffic into channels.** Put the real-time command and tick stream on one channel, and bulk transfers (saves, join data, traces) and chat on others, so a large transfer never delays commands.
- **Send state as "latest value" without guarantees.** The time-control packet already does this: a lost one is replaced by the next. Use the same approach for cursors and selections.
- **Allow for loss bursts.** Disconnect timeouts should survive several seconds of near-total loss, as on Wi-Fi or mobile links.

### 2. High ping (up to 500 ms)

- **Hide latency on the issuing client** (Factorio's "latency state"). Show the result of a player's own order straight away as a speculative layer:
  - designation and blueprint ghosts
  - the move target of a drafted pawn
  - zone painting
  - bill and settings changes in open windows

  Replace the speculative version when the confirmed command arrives. The underlying game state stays untouched, so this can't cause desyncs. In practice, Factorio shows wire connections instantly even when the real update arrives up to 2 s later.
- **Let commands target a future tick instead of "now on arrival"** (AoE's "turn + 2"). The client asks for execution at the predicted server tick plus a delay, and the server clamps that into a valid window. Delay becomes predictable and the same for everyone, rather than varying with each player's ping.

### 3. Jitter and smoothness

- **Size the buffer from measured jitter.** Replace the fixed 3 and 7 tick thresholds with a target buffer based on recent arrival jitter, for example a high percentile of the last N inter-arrival times, plus a margin. Fiedler uses about 100 ms; Overwatch makes the buffer bigger when the server detects loss.
- **Adjust speed smoothly.** Replace the jump between 0.8× and 1.2× with small, continuous adjustments of about ±2–10%. Abrupt jumps are what players notice as hitching.
- **Raise quickly, lower slowly** (AoE). Increase the buffer and turn delay immediately on a spike, then bring them back down over several seconds.
- **Don't let one bad connection slow everyone.** Today one lagging player pauses or slows the whole server. Factorio keeps going and schedules the slow player's input later. Better: let a lagging player catch up on their own using the existing simulation budget per frame, only pause everyone past a much larger threshold, and show who is lagging.
- **Spread ticks evenly across frames.** Bursty catch-up, many ticks in one frame and then none, looks worse than a steady slightly slower rate.

### 4. Detecting desyncs and recovering smoothly

- **Detect early.** The mod already compares state hashes regularly, which is the standard approach. Hashing each subsystem or map shows *what* diverged, not just *that* something did.
- **Make recovery smaller and less disruptive.** Factorio and the mod today both re-download the whole world. Ways to improve:
  1. **Resync one map at a time.** The mod already has async time per map. Hash each map separately and reload only the one that diverged plus world state.
  2. **Rejoin in the background.** Keep a cached snapshot plus its command log on the host so it doesn't freeze to serialise on demand. Stream it in chunks on the bulk channel. The desynced client shows a progress bar while everyone else keeps playing.
  3. **Bounded catch-up.** After loading, replay buffered commands with a budget per frame (a 25 ms budget already exists) and then hand over to normal play smoothly.
- **Treat desyncs as bugs.** Forrest Smith's point is that recovery hides symptoms and real fixes come from tooling. The traces and desync reports in [Client/Desyncs](../Source/Client/Desyncs) are where to invest.

## Testing a bad network

- LiteNetLib appears to have built-in network simulation (`SimulatePacketLoss`, `SimulateLatency`), but it is unconfirmed whether the 1.3.1 build used here includes it.
- For Steam connections, an external tool such as clumsy (Windows) is needed, because LiteNetLib's simulation doesn't apply to them.
- Reference test condition: 500 ms ±100 ms jitter with 5% loss. Metrics to log: buffer depth, stall count, stall length and catch-up bursts.
