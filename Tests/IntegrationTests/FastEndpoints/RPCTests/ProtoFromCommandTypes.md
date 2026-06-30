# Prototype: .proto from command types + pluggable wire format (issue #620)

Follows up on `GrpcReflection.md`, which showed reflection is empty because FE's
MessagePack methods have no protobuf descriptor. This explores the maintainer's
preferred path: generate a `.proto` from the command/result CLR types and make the
wire format pluggable, instead of FE owning a multi-language client generator.

## What it shows (`ProtoFromCommandTypes.cs`, 3 tests, all green)

- `Generates_Proto_From_A_Command_Type_With_No_Attributes` — a real proto3 schema
  generated straight from `EchoCommand`, no `[ProtoContract]`, no hand-authored
  `.proto`. The contractless model mirrors FE's MessagePack `ContractlessStandardResolver`
  (public members, alphabetical, numbered from 1). Generated output:

  ```proto
  syntax = "proto3";
  message EchoCommand {
     string FirstName = 1;
     string LastName = 2;
  }
  ```

- `Protobuf_Marshaller_RoundTrips_A_Real_FE_Command` — `ProtobufMarshaller<T>` is a
  drop-in twin of FE's `MsgPackMarshaller` (same shape, protobuf on the wire) and
  round-trips a real `EchoCommand` through the exact `Marshaller<T>` contract grpc-dotnet drives.

- `Primitive_Result_Needs_A_Wrapper` — the one wrinkle: `SomeCommand : ICommand<string>`.
  gRPC messages must be message types, so a bare scalar result has to be wrapped in a
  one-field message (protobuf-net.Grpc does this automatically). Shown concretely.

## How it plugs into FE (the wire format is now pluggable)

The wire format used to be hard-coded as `new MessagePackMarshaller<T>()` in two spots:

- server: `Src/Messaging/Messaging.Remote/Server/Commands/BaseHandlerExecutor.cs` (`Bind`)
- client: `Src/Messaging/Messaging.Remote.Core/Executors/BaseCommandExecutor.cs`

This branch replaces both with `RemoteMarshaller.Factory.Create<T>()`
(`Src/Messaging/Messaging.Remote.Core/MarshallerFactory.cs`). The factory defaults to
MessagePack, so every existing test still passes unchanged; assigning
`RemoteMarshaller.Factory` swaps the wire format for both client and server. That is the
"customization of the wire format" seam.

`ProtobufMarshaller<T>` in `ProtoFromCommandTypes.cs` is a working `IMarshallerFactory`
target: same shape as `MsgPackMarshaller`, protobuf on the wire.

> ponytail: the seam is a static hook to keep the change small. The upgrade path is a
> per-connection (`RemoteConnection`) and per-server (`GrpcServiceOptions`) option once a
> pluggable wire format becomes a shipped opt-in feature.

## Remaining piece: making reflection *list* a protobuf handler

A protobuf wire format is necessary but not sufficient for reflection. grpc-dotnet's
`AddGrpcReflection` builds its descriptor set by scanning endpoints and resolving a
`Google.Protobuf.Reflection.ServiceDescriptor` from each service type, via a
`BindServiceMethodAttribute` or a **static `Descriptor` property** (there is no public
registry to append to). FE's executors expose neither, so the last step is to generate a
`ServiceDescriptor` from the command/result types and surface it on the executor.

That generation is the invasive part: protobuf-net.Grpc's descriptor machinery
(`FileDescriptorSetFactory`) is `internal` and built around `[ServiceContract]` types, and
hand-building Google descriptors from the generated `.proto` hits a namespace collision
(protobuf-net.Reflection and Google.Protobuf both define `Google.Protobuf.Reflection.*`,
requiring `extern alias`). Tractable, but a dedicated pass rather than a one-liner.

## Tradeoffs to weigh

- Keep MessagePack as the default (fast, zero-config, FE-to-FE). Protobuf is opt-in per
  handler, only where cross-ecosystem interop is wanted.
- Field-number stability: alphabetical auto-numbering is fine for codegen-from-source, but
  a published contract needs stable numbers (explicit `[ProtoMember]` or a pinned manifest)
  if messages evolve.
- Primitive/`void` results need wrapper messages.

## Run

```powershell
dotnet test Tests/IntegrationTests/FastEndpoints/Int.FastEndpoints.csproj `
  --filter "FullyQualifiedName~RemoteProcedureCalls.ProtoFromCommandTypes"
```
