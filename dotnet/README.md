# LocoNet.Net

A managed .NET 10 port of the LocoNet2 protocol stack, designed for **LocoNet-over-TCP**
(no Arduino, no native UART). Talk to a LocoBuffer-USB exposed by `ser2net`, an
ESP32/Pico running this repo's firmware in bridge mode, or JMRI's LocoNetOverTcp server.

## Layout

| Project | Purpose |
| --- | --- |
| `LocoNet.Net` | Class library: opcodes, message buffer, TCP client. |
| `LocoNet.Net.Sample` | Console app that connects and prints every received frame. |

## Building

```pwsh
dotnet build dotnet/LocoNet.sln
```

## Running the sample

```pwsh
dotnet run --project dotnet/LocoNet.Net.Sample -- <host> <port>
```

## What is implemented so far

- `OpCode` enum + length helper (`OpCodeExtensions.PacketSize`).
- `LnConstants` — bit-mask and field constants from `src/ln_opc.h`.
- `LnMsg` — immutable frame value type with checksum compute/verify.
- `LocoNetMessageBuffer` — byte-stream framer ported from
  [`src/LocoNetMessageBuffer.cpp`](../src/LocoNetMessageBuffer.cpp).
- `LocoNetTcpClient` — raw-binary LocoNet-over-TCP client with read/write loops
  exposed as events.

## Not yet ported

Higher-level helpers — Throttle, Turnout, FastClock, CV access, SV access — and the
JMRI ASCII `SEND`/`RECEIVE` wire format. Track progress against
[`docs/SPECIFICATION_COMPLIANCE.md`](../docs/SPECIFICATION_COMPLIANCE.md).
