# LocoNet (.NET)

A managed **.NET 10** port of the [LocoNet2](https://github.com/mrrwa/LocoNet2)
Arduino library, focused on **LocoNet-over-TCP**. Talk to a LocoBuffer-USB exposed
by `ser2net`, an ESP32 / RP2040 running LocoNet2 firmware in TCP-bridge mode, or
JMRI's `LocoNetOverTcp` server — from any platform .NET runs on (Windows, Linux,
macOS, containers).

The original C++ Arduino sources for ESP32/Pico/STM32/UnoR4 are preserved in this
fork so the protocol behaviour can be cross-checked against the reference
implementation, but the .NET projects under [`dotnet/`](dotnet/) are the primary
focus.

## Why a managed port?

- Run model-railway tooling on a Raspberry Pi, a NAS, or a desktop without an
  Arduino in the loop.
- Get strong typing, async/await, structured cancellation, and modern testing
  (xUnit) on top of a protocol stack originally written for 8/32-bit MCUs.
- Re-use the same library from desktop throttles, headless services, dispatch
  systems, automated test rigs, and CI.

## Repository layout

| Path | Purpose |
| --- | --- |
| `dotnet/LocoNet.Net` | Class library — opcodes, messages, framing, dispatcher, throttle, fast clock, CV/SV access, JMRI parser, resilient TCP client. |
| `dotnet/LocoNet.Net.Tests` | xUnit test suite (133 tests, network resilience tests use loopback `TcpListener`). |
| `dotnet/LocoNet.Net.Sample` | Console app demonstrating connect, auto-reconnect, idle watchdog, and stats. |
| `src/` | Original Arduino C++ LocoNet2 implementation (upstream reference). |
| `examples/` | Original Arduino sketches for ESP32 / Pico / STM32 / Uno R4. |
| `docs/SPECIFICATION_COMPLIANCE.md` | Feature matrix vs. the official LocoNet personal-edition spec. |

## What's implemented in the .NET port

- **Wire layer** — `OpCode`, `LnConstants`, `LnMsg` (immutable frame value type
  with checksum compute/verify), `LocoNetMessageBuffer` (byte-stream framer).
- **Dispatch** — `LocoNetDispatcher` for fan-out to consumer callbacks with
  opcode filtering.
- **High-level helpers** — `LocoNetThrottle`, `LocoNetTurnout`,
  `LocoNetFastClock`, `LocoNetCVAccess`, `LocoNetSystemVariable`,
  `LongAckAwaiter`.
- **Transports** —
  - `LocoNetTcpClient`: raw binary LocoNet-over-TCP with **auto-reconnect**
    (exponential backoff + jitter), per-attempt connect timeout, OS-level TCP
    keepalives, idle-read watchdog, bounded TX queue with configurable
    backpressure, TX rate cap, `StateChanged` / `Reconnected` events, and full
    `LnConnectionStats`.
  - `LocoNetJmriTcpClient`: JMRI ASCII `SEND` / `RECEIVE` wire format.
- **JMRI line parser** — `JmriLineParser`.

Track feature parity in
[`docs/SPECIFICATION_COMPLIANCE.md`](docs/SPECIFICATION_COMPLIANCE.md).

## Quick start

```pwsh
# Build everything
dotnet build dotnet/LocoNet.slnx

# Run tests
dotnet test dotnet/LocoNet.slnx

# Connect to a LocoNet-over-TCP server and print received frames
dotnet run --project dotnet/LocoNet.Net.Sample -- <host> <port>
```

## Example: resilient client

```csharp
using LocoNet.Net;

var options = new LocoNetTcpClientOptions
{
    InitialReconnectDelay = TimeSpan.FromMilliseconds(500),
    MaxReconnectDelay     = TimeSpan.FromSeconds(15),
    IdleReadWatchdog      = TimeSpan.FromSeconds(30),
    EnableTcpKeepAlives   = true,
    TxMessagesPerSecond   = 50,
    Logger                = (level, msg, ex) => Console.WriteLine($"[{level}] {msg}"),
};

await using var client = new LocoNetTcpClient("locobuffer.local", 1234, options);

client.MessageReceived += (_, e) => Console.WriteLine($"RX {e.Message}");
client.StateChanged    += (_, e) => Console.WriteLine($"{e.Previous} -> {e.Current}");
client.Reconnected     += (_, e) => Console.WriteLine($"Reconnected (downtime {e.Downtime})");

await client.ConnectAsync();
```

## Relationship to upstream

This repository is a fork of [mrrwa/LocoNet2](https://github.com/mrrwa/LocoNet2)
(itself a refactor of the original Arduino [LocoNet](https://github.com/mrrwa/LocoNet)
library by Alex Shepherd, Stefan Bormann, Damian Philipp, John Plocher, et al.).
The Arduino C++ sources under `src/` and `examples/` are kept in sync with
upstream where possible; the `dotnet/` tree is new and lives only in this fork.

## License

Inherits the upstream LocoNet2 license. See the project files for details.
