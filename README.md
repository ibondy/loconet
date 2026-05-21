# LocoNet.Net

Managed **.NET 10** implementation of the LocoNet protocol over TCP. Talk to a
LocoBuffer-USB exposed by `ser2net`, an ESP32 / RP2040 running LocoNet2 firmware
in TCP-bridge mode, or JMRI's `LocoNetOverTcp` server — from any platform .NET
runs on (Windows, Linux, macOS, containers).

This is a managed port of the [mrrwa/LocoNet2](https://github.com/mrrwa/LocoNet2)
Arduino library. The original C++ sources are kept under [`src/`](src/) and
[`examples/`](examples/) for cross-reference; the .NET projects live under
[`dotnet/`](dotnet/).

## Projects

| Project | Purpose |
| --- | --- |
| `LocoNet.Net` | Class library: opcodes, framing, dispatcher, throttle, fast clock, CV / SV access, JMRI parser, resilient TCP client. |
| `LocoNet.Net.Tests` | MSTest suite — 133 tests including loopback TCP resilience tests. |
| `LocoNet.Net.Sample` | Console demo exercising auto-reconnect, watchdog, and stats. |

## Build & test

```pwsh
dotnet build dotnet/LocoNet.slnx
dotnet test  dotnet/LocoNet.slnx
```

## Run the sample

```pwsh
dotnet run --project dotnet/LocoNet.Net.Sample -- <host> <port>
```

The sample subscribes to `MessageReceived`, `StateChanged`, and `Reconnected`,
sends a `GpOn` (global power on), and prints `LnConnectionStats` every 10 s.

## `LocoNetTcpClient` resilience features

Configured via `LocoNetTcpClientOptions`:

- **Auto-reconnect** with exponential backoff + jitter (`InitialReconnectDelay`,
  `MaxReconnectDelay`, `ReconnectJitter`, `MaxReconnectAttempts`).
- **Per-attempt connect timeout** (`ConnectTimeout`, default 10 s).
- **OS TCP keepalives** (`EnableTcpKeepAlives`, `TcpKeepAliveTime`,
  `TcpKeepAliveInterval`, `TcpKeepAliveRetryCount`).
- **App-level idle-read watchdog** (`IdleReadWatchdog`) to catch half-open
  sockets that keepalives miss.
- **Bounded TX queue** with configurable backpressure policy (`TxQueueCapacity`,
  `TxFullMode`).
- **Disconnect TX policy** — discard or throw (`DisconnectBehavior`). Pending
  frames are **always** dropped across a reconnect, by design.
- **TX rate cap** (`TxMessagesPerSecond`).
- **Events** — `MessageReceived`, `Disconnected`, `StateChanged`, `Reconnected`.
- **Stats** — `RxStats`, `TxStats`, and `ConnectionStats` (`Connects`,
  `Reconnects`, `FailedAttempts`, `Drops`, `IdleWatchdogTrips`,
  `TxDiscardedWhileDisconnected`, `TxDroppedByBackpressure`, `LastRxUtc`,
  `LastTxUtc`, `LastError`).
- **Logging hook** — `Logger` delegate (`LocoNetLogLevel`).

## Feature parity

See [`docs/SPECIFICATION_COMPLIANCE.md`](docs/SPECIFICATION_COMPLIANCE.md)
for the .NET implementation matrix against the LocoNet personal-edition
specification.
