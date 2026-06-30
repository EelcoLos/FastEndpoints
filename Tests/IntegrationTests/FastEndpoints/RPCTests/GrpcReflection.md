# Prototype: gRPC reflection on a FastEndpoints handler server (issue #620)

## What this branch does
- Adds `Grpc.AspNetCore.Server.Reflection` to `TestHarness/Web` and wires up
  `AddGrpcReflection()` + `MapGrpcReflectionService()` (2 lines in `Program.cs`).
- Adds `GrpcReflection.cs` — two in-process tests that query the live reflection
  service through the same handler the RPC tests use.

## What it shows
Reflection turns on fine, but it can only see services that carry a real
`Google.Protobuf.Reflection.ServiceDescriptor`:

- `Reflection_Endpoint_Is_Reachable_But_Lists_No_FE_Handlers` — the reflection
  service lists **itself** (`grpc.reflection.v1alpha.ServerReflection`) but **none**
  of the registered FE commands (`SomeCommand`, `EchoCommand`, `VoidCommand`).
- `Describing_A_FE_Command_Service_Fails` — asking for the descriptor of a FE
  command returns an `ErrorResponse`, so `grpcurl describe` / Postman cannot
  build a request.

## Why
FE binds methods dynamically via `IServiceMethodProvider` with a
`MessagePackMarshaller` (`Src/Messaging/Messaging.Remote.Core/MsgPackMarshaller.cs`)
and **no proto descriptors**. grpc-dotnet's reflection resolves descriptors via
`BindServiceMethodAttribute` / a generated `Descriptor` property; FE has neither,
so it logs *"Could not resolve service descriptor for '...'"* and skips them.

Even synthesizing descriptors wouldn't help: the wire format is MessagePack+LZ4,
so any protobuf descriptor would describe a message the server can't deserialize.
Useful reflection requires a protobuf wire format (i.e. protobuf-net.Grpc-style),
which is a transport change, not an add-on.

## Run
```
dotnet test Tests/IntegrationTests/FastEndpoints/Int.FastEndpoints.csproj \
  --filter "FullyQualifiedName~RemoteProcedureCalls.GrpcReflection"
```
