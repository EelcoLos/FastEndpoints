# Path A: FE generates descriptors from command types (issue #620)

One of the two descriptor-generation approaches the maintainer asked to compare. Both branches fork
from the pluggable-marshaller foundation on `prototype/grpc-reflection-620`.

Path A keeps handlers as attribute-free `ICommand<T>` and has FE own the descriptor generation. A
`Google.Protobuf.Reflection.ServiceDescriptor` is built at runtime straight from a command's CLR types,
then handed to the stock `Grpc.Reflection` service, which lists and describes the handler under the exact
service name FE binds it to.

## What it shows (`DescriptorGenReflection.cs`, 2 tests, green)

- `Generates_A_Real_ServiceDescriptor_From_A_Command_Type` - builds a real `ServiceDescriptor` from
  `EchoCommand`. Its `FullName` is `TestCases.CommandBusTest.EchoCommand` (the same service name FE binds
  the handler under), and the request message's fields are `[FirstName, LastName]` - the same contractless
  shape FE's MessagePack resolver uses (public props, alphabetical, numbered from 1), no attributes.

- `Reflection_Lists_And_Describes_A_Handler_From_Generated_Descriptors` - stands up an in-process gRPC
  server (`WebApplication` + `UseTestServer`), seeds the stock reflection service with the generated
  descriptors, and queries it. Observed reflection `ListServices`:

  ```
  TestCases.CommandBusTest.EchoCommand
  ```

  and `FileContainingSymbol` for that service returns a real `FileDescriptorProto` - a grpcurl `describe`
  succeeds against a FastEndpoints command, which the foundation's `GrpcReflection.cs` proves it cannot do today.

## How it works

`CommandDescriptorFactory.BuildFile(commandType, resultType)` builds one self-contained proto3
`FileDescriptorProto` (package = command namespace, a service named after the command's simple name so its
FQN equals the command's `FullName`, one method, and a message per distinct request/result CLR type), then
`FileDescriptor.BuildFromByteStrings(...)` compiles it. The resulting `ServiceDescriptor`s feed
`Grpc.Reflection.ReflectionServiceImpl` (v1alpha) and `ReflectionV1ServiceImpl` (v1), both mapped as gRPC
services. This bypasses `AddGrpcReflection()`'s endpoint auto-discovery, which finds no descriptor for FE's
dynamically-bound MessagePack methods.

Notes:

- Hand-building with `Google.Protobuf`'s own descriptor types avoids the protobuf-net.Reflection vs
  Google.Protobuf `Google.Protobuf.Reflection.*` namespace collision entirely - no `extern alias` needed.
  (That collision was the blocker flagged earlier for reusing protobuf-net's descriptor machinery.)
- The manually-seeded reflection service lists exactly the descriptors it is given; it does not advertise
  itself the way the auto-discovery path does. That is fine - clients still `list`/`describe` the handlers.
- ponytail: the factory maps scalar fields (enough for `EchoCommand` and the common case). Nested message
  fields and collections are the next increment: emit a message per nested type and set `Type.Message` + `TypeName`.

## The cost (this is the thing to weigh against path B)

FE owns the descriptor-generation code (the `CommandDescriptorFactory` here, plus, for a real feature, wiring
it into handler-server startup to seed the reflection service from the registered handlers, and deciding
field-number stability for published contracts). In return, handlers stay attribute-free `ICommand<T>` - no
`[Service]`/`[DataMember]` reshaping, no second registration model.

## Path A vs path B in one line

- **Path A (this branch):** handlers stay attribute-free `ICommand<T>`; FE owns descriptor generation from
  the CLR types and serves them via `Grpc.Reflection`.
- **Path B (`...-b-protobuf-net-grpc`):** almost no FE-owned code; protobuf-net.Grpc owns descriptors +
  reflection, but handlers move onto a `[Service]`-shaped, field-numbered surface.

## Run

```powershell
dotnet test Tests/IntegrationTests/FastEndpoints/Int.FastEndpoints.csproj `
  --filter "FullyQualifiedName~RemoteProcedureCalls.DescriptorGenReflection"
```
