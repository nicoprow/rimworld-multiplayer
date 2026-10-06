# CLAUDE.md

## Project

This is the RimWorld Multiplayer mod. It uses deterministic lockstep: every client simulates the full game and only commands go over the network, with the host's server deciding which tick each command runs on.

Source is in `Source/` (`Client`, `Common`, `Server`, `Tests`). Build with `dotnet build Source/Multiplayer.sln`.

## Current initiative: netcode resilience

Goal: keep gameplay smooth at up to 500 ms ping, with packet loss and jitter, and make desync recovery smoother and less disruptive for everyone else in the game.

Planned direction:
- stop a single lost packet from stalling commands (separate channels, redundant command sending)
- size the tick buffer from measured jitter, with smooth speed adjustment
- stop one lagging player from slowing everyone
- show a player's own orders instantly (latency hiding)
- rejoin in the background, and later resync one map at a time

Keep deterministic lockstep. Rollback and state syncing are ruled out (see research).

- Research and sources: [docs/netcode-research.md](docs/netcode-research.md)
- Working task list: [docs/netcode-tasks.md](docs/netcode-tasks.md)

## Working rules

### Task file
- `docs/netcode-tasks.md` always shows the current state. Overwrite outdated information in place: statuses, findings, open questions, decisions.
- Never keep a log or history in it. No "changelog", "previously", or dated entries. Git commits are the history.
- When research answers a question, replace the question with the answer. Remove what no longer applies.

### Git
- Commit directly on `master`. Don't create feature branches for this work.

### Code style
- Never write comments in code.
- Express intent through descriptive names for functions, variables, types and parameters.
- For complex logic, don't explain it in comments. Write it out more verbosely instead: break it into small intermediate steps, with well-named intermediate variables and small helper functions that make the intent obvious.
- Match the surrounding code's conventions otherwise.
