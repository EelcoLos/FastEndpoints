using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;

namespace RemoteProcedureCalls;

// prototype for issue #620: enable standard grpc reflection on a FastEndpoints handler server
// and observe what reflection clients (grpcurl/postman) actually see.
//
// the handler server (TestHarness/Web) registers several command handlers + event hubs, and now
// also calls AddGrpcReflection()/MapGrpcReflectionService(). these tests query the live reflection
// service in-process to demonstrate the gap described on the issue.
public class GrpcReflection(Sut App) : TestBase<Sut>
{
    ServerReflection.ServerReflectionClient ReflectionClient()
    {
        var channel = GrpcChannel.ForAddress(
            "http://localhost", //ignored - the in-memory test handler below does the actual transport
            new GrpcChannelOptions { HttpHandler = App.CreateHandler() });

        return new ServerReflection.ServerReflectionClient(channel);
    }

    static async Task<ServerReflectionResponse> SingleAsync(ServerReflection.ServerReflectionClient client, ServerReflectionRequest req)
    {
        using var call = client.ServerReflectionInfo();
        await call.RequestStream.WriteAsync(req);
        await call.RequestStream.CompleteAsync();
        await call.ResponseStream.MoveNext(default);

        return call.ResponseStream.Current;
    }

    [Fact]
    public async Task Reflection_Endpoint_Is_Reachable_But_Lists_No_FE_Handlers()
    {
        var client = ReflectionClient();
        var res = await SingleAsync(client, new() { ListServices = "" });

        var services = res.ListServicesResponse.Service.Select(s => s.Name).ToList();

        // the reflection service can describe ITSELF (it has a real protobuf descriptor)...
        services.ShouldContain("grpc.reflection.v1alpha.ServerReflection");

        // ...but NONE of the registered FastEndpoints command handlers appear, because they are
        // bound dynamically via IServiceMethodProvider with a MessagePack marshaller and have no
        // Google.Protobuf ServiceDescriptor for the reflection service to resolve.
        // (grpc-dotnet logs: "Could not resolve service descriptor for '...'")
        services.ShouldNotContain("TestCases.CommandBusTest.SomeCommand");
        services.ShouldNotContain("TestCases.CommandBusTest.EchoCommand");
        services.ShouldNotContain("TestCases.CommandBusTest.VoidCommand");
    }

    [Fact]
    public async Task Describing_A_FE_Command_Service_Fails()
    {
        var client = ReflectionClient();
        var res = await SingleAsync(client, new() { FileContainingSymbol = "TestCases.CommandBusTest.SomeCommand" });

        // there is no proto file descriptor for a FE command, so reflection returns an error response
        // instead of a FileDescriptorProto - a grpcurl `describe` against this symbol fails here.
        res.MessageResponseCase.ShouldBe(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse);
    }
}
