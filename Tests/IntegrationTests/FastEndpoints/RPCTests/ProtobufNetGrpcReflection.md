# Path B: lean on protobuf-net.Grpc's code-first stack (issue #620)

One of the two descriptor-generation approaches the maintainer asked to compare. Both branches
fork from the pluggable-marshaller foundation on `prototype/grpc-reflection-620`.

Path B does **not** generate descriptors itself. A command handler is expressed as a code-first
protobuf-net.Grpc `[Service]`, and protobuf-net.Grpc's own stack does everything else: it builds
the protobuf descriptors from the CLR types and serves standard gRPC reflection. grpcurl/Postman
then list and describe the service with no hand-authored `.proto`.

## What it shows (`ProtobufNetGrpcReflection.cs`, 1 test, green)

`Reflection_Lists_And_Describes_A_Code_First_Handler` stands up an in-process gRPC server
(`WebApplication` + `UseTestServer`, isolated from FE's own gRPC), maps a code-first service, adds
code-first reflection, then queries the reflection service as a client. Observed reflection output:

```
grpc.reflection.v1alpha.ServerReflection
TestCases.CommandBusTest.EchoCommand
```

The command service is listed, and `FileContainingSymbol` returns a real `FileDescriptorProto`
(a grpcurl `describe` succeeds) - exactly what FE's MessagePack handlers cannot do.

## Wiring

```csharp
builder.Services.AddCodeFirstGrpc();            // protobuf-net.Grpc.AspNetCore
builder.Services.AddCodeFirstGrpcReflection();  // protobuf-net.Grpc.AspNetCore.Reflection
...
app.MapGrpcService<EchoCommandService>();
app.MapCodeFirstGrpcReflectionService();        // must be mapped after the services it reflects over
```

Packages: `protobuf-net.Grpc.AspNetCore` 1.2.2 + `protobuf-net.Grpc.AspNetCore.Reflection` 1.2.2
(both in namespace `ProtoBuf.Grpc.Server`). Reflection is a **separate** package and uses
protobuf-net's own reflection service. Google's `Grpc.AspNetCore.Server.Reflection`
(`AddGrpcReflection`) is not used here - it does not see code-first contracts.

## The cost (this is the thing to weigh against path A)

To be reflectable this way, a handler must be reshaped into a code-first contract:

- a `[Service]` interface surfaces the operation (protobuf-net.Grpc's own attribute, so no
  `System.ServiceModel` dependency)
- the request/result types need protobuf field numbers - `[DataMember(Order = n)]` (BCL) or
  `[ProtoMember(n)]`

FE commands are attribute-free `ICommand<T>` today, so this path means either annotating command
types or projecting them onto contract DTOs (the test uses an `EchoRequest`/`EchoReply` pair mirroring
`EchoCommand`). Field numbers also need to stay stable if a published contract evolves.

## Path B vs path A in one line

- **Path B (this branch):** almost no FE-owned code; protobuf-net.Grpc owns descriptors + reflection.
  Cost: handlers move onto a `[Service]`-shaped, field-numbered surface, i.e. a second registration
  model alongside FE's native handler pipeline.
- **Path A (`...-a-descriptor-gen`):** FE generates `Google.Protobuf` descriptors from the existing
  command types and serves them via `Grpc.Reflection`. Cost: FE owns the descriptor-generation code,
  but handlers stay attribute-free `ICommand<T>`.

## Run

```powershell
dotnet test Tests/IntegrationTests/FastEndpoints/Int.FastEndpoints.csproj `
  --filter "FullyQualifiedName~RemoteProcedureCalls.ProtobufNetGrpcReflection"
```
