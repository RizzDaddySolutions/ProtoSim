# ProtoSim

ProtoSim is an industrial protocol simulator for Windows (C#, .NET 10, WPF with the built-in Fluent theme). It simulates devices that real clients (SCADA, HMI, PLC programs) can talk to. Each protocol is a plugin. **Modbus TCP** and **OPC UA** are included.

![Simulated OPC UA device](docs/screenshots/opcua-device.png)

## Features

**Simulated devices (servers)**
- Run any number of devices at once, mixing protocols, each on its own port, with a start/stop control and live status.
- **Tags** map typed values (Bool, Int16/32/64, UInt16/32/64, Float32/64, String) onto protocol addresses: Modbus registers, or OPC UA variables in a folder tree.
- **Value simulation** per tag: ramp, sine, triangle, square, random, random walk, counter and toggle, each with its own min/max/period/step/update rate. Tags can be imported and exported as CSV.
- **Traffic log** with decoded summaries (and hex bytes for Modbus), filtering, pause, auto-scroll, and export to .log/.csv.
- **Connections and statistics**: connected clients or sessions (with a button to drop each one), requests/s, errors and unanswered requests.
- **Fault injection** (applies while running): response delay and jitter, timeouts, error responses, and random disconnects.

**Projects**
- Save and open `.psim` files (JSON). A project holds devices, settings, faults, tags and their values, and Modbus memory. Older files that contain client sessions still open; the sessions are ignored. A device can be set to start automatically when the project opens.
- Command line: `ProtoSim.exe project.psim` opens a project. `ProtoSim.exe --demo modbus-tcp` or `--demo opc-ua` starts a running demo device with sample tags.
- Light, dark or system theme (View menu).

## Modbus TCP

![Modbus device data view](docs/screenshots/modbus-device.png)

| Function | Code |
|---|---|
| Read Coils / Discrete Inputs | 01 / 02 |
| Read Holding / Input Registers | 03 / 04 |
| Write Single Coil / Register | 05 / 06 |
| Diagnostics: Return Query Data | 08/00 |
| Write Multiple Coils / Registers | 15 / 16 |
| Mask Write Register | 22 |
| Read/Write Multiple Registers | 23 |
| Read Device Identification (basic, regular, individual) | 43/14 |

- Raw memory tables for coils, discrete inputs, input registers and holding registers. Values can be shown as unsigned, signed, hex, binary or ASCII, edited in place, and filled, cleared, randomized or numbered in bulk. Values that just changed are highlighted.
- Requests are checked against the spec's quantity limits and return the correct exception codes. Other settings: which unit IDs to answer (`*`, `1,2,10-20`), gateway exceptions 0x0A/0x0B for other unit IDs, unit 0 as broadcast, read-only mode, the size of each memory area, and the device identification strings.
- Tag addresses: `HR:0`, `IR:0`, `CO:0`, `DI:0` (0-based offsets), `40001` / `400001` (Modicon, 1-based), and `HR:10.3` (one bit of a register). Multi-register values can use any word order (ABCD, CDAB, BADC, DCBA).

### Modbus RTU

**Modbus RTU (serial)** runs a slave on a COM port (default 19200 8E1, unit 1, frames end after 3.5 character times), and **Modbus RTU over TCP** carries the same RTU frames (unit + PDU + CRC-16, no MBAP header) over TCP, like a serial device server. Both share the Modbus TCP request handler, memory tables and tag addresses. Bad-CRC frames are ignored, other unit IDs get no reply by default, and unit 0 is a silent broadcast. Use a virtual COM port pair such as com0com to test the serial slave without hardware.

## OPC UA

Built on the official OPC Foundation .NET Standard stack (1.5.378).

- **Server:** each tag becomes a variable node under `Objects/<root folder>`. A tag address like `Plant/Tank1/Level` creates the folders and the NodeId `ns=2;s=Plant/Tank1/Level`. Nodes appear and disappear live as tags are added or removed, and simulated values are pushed to subscriptions.
- **Security:** None, Basic256Sha256 Sign, and Sign & Encrypt. Login can be anonymous, with a user name and password, or both. A self-signed application certificate is created on first start in `%LocalAppData%\ProtoSim\pki`, and untrusted client certificates can be accepted automatically.
- **Settings:** host name, port (4840), endpoint path, namespace URI, root folder, read-only mode, maximum sessions and minimum sampling interval.
- **Fault injection:** delay, simulated timeouts (`BadTimeout`), error responses (codes are mapped to OPC UA status codes such as `BadInternalError` and `BadNodeIdUnknown`), and closing sessions.

## Build and run

Requires the .NET 10 SDK on Windows. NuGet restores the OPC UA packages on the first build.

```bash
dotnet build ProtoSim.slnx
```

```bash
dotnet test ProtoSim.slnx
```

```bash
dotnet run --project src/ProtoSim.App -- --demo opc-ua
```

Build output goes to `artifacts/`. If the repository sits in a very deep folder, it goes to `%TEMP%\ProtoSim\artifacts` instead, to stay under the Windows 260-character path limit.

## REST API

A local-only JSON API simulator (listens on `127.0.0.1`/`::1` only) with a configurable base URL, default `http://localhost:5080/api/v1`.

- **Bearer tokens**: OAuth 2.0 client credentials endpoint (`/oauth/token`) issuing HS256 JWTs. Each client has its own client ID, secret and optional scopes, managed on the **API clients** tab. Authentication can be turned off.
- **Simulated GET endpoints**: each tag's address is a path (e.g. `sensors/temperature`), simulated like any other protocol; PUT/PATCH/POST to the path writes the value.
- **JSON database** for everything else: `GET/POST /users`, `GET/PUT/PATCH/DELETE /users/{id}`, with json-server style filtering, sorting and paging. It is created once per device in `%LocalAppData%\ProtoSim\RestDatabases`, persists every change, and can be edited, recreated, exported and imported on the **Database** tab.

## BACnet/IP

A BACnet/IP device (UDP 47808) built on the MIT-licensed [BACnet](https://github.com/ela-compil/BACnet) stack.

- Tag addresses are objects: `AI:1`, `AO:1`, `AV:3/Zone Setpoint`, `BI`/`BO`/`BV` (Bool), `MSI`/`MSO`/`MSV` (1..N), `CSV:1` (String).
- Who-Is/I-Am, Who-Has/I-Have, ReadProperty(Multiple) including ALL, WriteProperty(Multiple) with a 16-level priority array and relinquish default, Out_Of_Service on inputs.
- SubscribeCOV / SubscribeCOVProperty with COV increment, DeviceCommunicationControl, ReinitializeDevice and TimeSynchronization.

## SNMP agent

An SNMPv1/v2c agent (UDP 161) for simulating UPSs, PDUs, switches and other monitored equipment. Messages are encoded with the MIT-licensed [#SNMP](https://github.com/lextudio/sharpsnmplib) library.

- Tag addresses are scalar OIDs, e.g. `1.3.6.1.4.1.99999.1.1.0`, with an optional `:counter32`, `:gauge32`, `:timeticks`, `:counter64`, `:ipaddress`, `:truthvalue` or `:string` syntax.
- Built-in system group (sysDescr, sysObjectID, sysUpTime, sysContact, sysName, sysLocation, sysServices).
- Get, GetNext, GetBulk and atomic Set with v1 and v2c error semantics; read and write community strings.
- v2c traps: coldStart on start and, optionally, a trap carrying the values of each Set.

## MTConnect

An MTConnect agent (HTTP, port 5000) for a simulated machine tool.

- Tag addresses are data items in a component tree: `Axes/Linear[X]/Xact:POSITION.ACTUAL`, `Controller/Path/execution:EXECUTION`, `Controller/system:SYSTEM`. The category (sample, event or condition) and default units come from the type, and can be overridden with `:SAMPLE`, `:EVENT` or `:CONDITION`.
- `probe`, `current` (with `at`), `sample` (with `from` and `count`) and `assets`, with or without the device name in the URL, and `path` filters evaluated as real XPath over the probe document.
- Every value change becomes a numbered observation in a circular buffer (unchanged values are skipped unless configured). The agent reports `firstSequence`, `lastSequence` and `nextSequence`, and returns MTConnect errors such as `OUT_OF_RANGE`, `INVALID_XPATH` and `NO_DEVICE`.
- XML by default, or JSON (the reference agent's JSON version 2 layout) with `Accept: application/json` or `?format=json`; the device's default format is a setting.
- Streaming (`interval`) isn't supported; clients poll `current` or `sample`.

## Scripted device

A text request/reply device over TCP, UDP or a serial port for instruments, scales, barcode readers and legacy ASCII protocols. Behaviour comes from rules in the device settings, one per line:

```
*IDN?                      => ProtoSim,Virtual Instrument,SN0001,1.0
MEAS:TEMP?                 => {Temperature:F2}
/SOUR:VOLT (?<v>[-0-9.]+)/ => {Setpoint=$v}OK
OUTP?                      => {Output:1/0}
```

- Plain patterns match the whole request; `/regex/` patterns capture groups for `{$1}` / `{$name}`.
- `{Tag}` / `{Tag:format}` insert tag values, `{Tag=$1}` writes them, `{now:HH:mm:ss}` inserts the time; `\r \n \t \xHH` escapes.
- Framing by CR, LF, any newline, a custom terminator or a silent gap; a reply terminator, optional echo, unknown-command and error replies.
- A greeting on connect and a periodic message (e.g. a scale streaming weight) for unsolicited output.

## Sparkplug B

A Sparkplug B edge node (`spBv1.0/{group}/.../{edge node}`) that can run its own MQTT broker or connect to an external one.

- Tag addresses are metric names: `Inputs/Temperature` for an edge node metric, `Pump1:Speed` for a metric of device `Pump1`.
- NBIRTH/DBIRTH on connect, NDEATH with `bdSeq` as the MQTT will, report-by-exception NDATA/DDATA with `seq`, optional aliases.
- NCMD/DCMD write metrics; `Node Control/Rebirth` and tag changes trigger rebirths; optional primary host STATE awareness.
- Payloads are encoded with a small built-in protobuf codec (no generated code).

## Architecture

```
src/
  ProtoSim.Core                 Protocol-agnostic: plugin contracts, data model, register codec,
                                simulation engine, traffic log, statistics, fault settings, project format
  ProtoSim.Protocols.ModbusTcp  Modbus TCP, RTU (serial) and RTU-over-TCP plugins: data model, request handler, servers
  ProtoSim.Protocols.OpcUa      OPC UA plugin: variable model, node manager, server
  ProtoSim.Protocols.Rest       REST API plugin: loopback HTTP/1.1 server, OAuth 2.0 client credentials,
                                tag endpoints, JSON document database
  ProtoSim.Protocols.Bacnet     BACnet/IP plugin: object model, priority arrays, COV, on the BACnet NuGet stack
  ProtoSim.Protocols.MTConnect  MTConnect agent plugin: HTTP server, component tree, observation buffer, XPath filters
  ProtoSim.Protocols.Script     Scripted device plugin: rule engine and templates over TCP, UDP or serial
  ProtoSim.Protocols.Snmp       SNMP agent plugin: MIB view of system group and tag OIDs, v1/v2c requests, traps
  ProtoSim.Protocols.SparkplugB Sparkplug B plugin: protobuf payload codec, edge node over MQTTnet, embedded broker
  ProtoSim.App                  WPF UI (MVVM); knows protocols only through IProtocolPlugin
tests/
  ModbusTcp.Tests               Codec, addressing, request handler, RTU framing/CRC and loopback tests (with a test-only client)
  OpcUa.Tests                   Variable model and loopback tests with a test-only client (browse, read, write, subscribe, security, login)
  Rest.Tests                    Token, auth, tag, CRUD/query, persistence/export and loopback HTTP tests
  Bacnet.Tests                  Address rules and loopback tests with a BACnet client (discovery, reads, commands, COV, DCC, faults)
  MTConnect.Tests               Address rules and HTTP tests for probe, current, sample, XPath, buffer wrap, errors and faults
  Script.Tests                  Rule parsing, templates, writes, framing, and TCP/UDP request/reply, greeting and periodic output
  Snmp.Tests                    OID/syntax rules, Get/GetNext/GetBulk/Set semantics, communities, faults, UDP and traps
  SparkplugB.Tests              Payload codec, addressing, and births/data/commands/STATE against the embedded broker
```

The UI never references protocol types. Everything it shows comes from these Core abstractions:

- `IProtocolPlugin`: the protocol's entry point. It creates settings, data models and servers.
- `IDeviceDataModel`: the device memory. Register protocols expose `IDataArea` tables, which the UI shows as the Data tab. Node-based protocols such as OPC UA expose no tables and work through tags only.
- `ITagBinding`: a typed value at a protocol-specific address, which the simulation engine drives. It is disposed when a tag is removed, so node-based protocols can delete the node.
- `IProtocolServer`: the running simulated device.
- Settings and request objects are plain classes. A reflection-based property grid edits them (using `[Display]`, `[Range]` and `[Browsable(false)]` attributes), and they are saved as JSON. **A new protocol needs no WPF code.**

## Adding a protocol

1. Create a class library that references `ProtoSim.Core`.
2. Implement `IProtocolPlugin`, plus `IDeviceDataModel` and `IProtocolServer`. `RegisterCodec`, `TrafficLog`, `ServerStatistics` and `FaultSettings` can be reused.
3. Either drop the built DLL into a `Plugins` folder next to `ProtoSim.exe` (it is discovered at startup), or register the plugin in `App.xaml.cs` as is done for the built-in plugins.

Natural next protocols: MTConnect streaming and assets, and IEC 61850.
