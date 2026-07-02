using System.Runtime.Serialization;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Configuration;
using ProtoBuf.Grpc.Server;

namespace RemoteProcedureCalls;

// prototype for issue #620, PATH B of the descriptor bake-off: lean on protobuf-net.Grpc's code-first stack.
//
// instead of FE generating descriptors itself (path A), a command handler is expressed as a code-first
// [ServiceContract]. protobuf-net.Grpc generates protobuf descriptors from the CLR types and serves standard
// grpc reflection for free - via its OWN reflection service, not Google's (Google's AddGrpcReflection does not
// see code-first contracts). grpcurl/postman then list + describe the service with no hand-authored .proto.
//
// the cost to weigh against path A: the command/result must be a field-numbered contract ([DataMember(Order = n)])
// surfaced through a [ServiceContract], rather than FE's attribute-free ICommand<T> handlers. that reshaping is
// the price of getting the whole descriptor + reflection pipeline handed to you.
public class ProtobufNetGrpcReflection(ITestOutputHelper output)
{
    [Fact]
    public async Task Reflection_Lists_And_Describes_A_Code_First_Handler()
    {
        await using var server = await StartServerAsync();
        var client = ReflectionClient(server);

        var listed = await SingleAsync(client, new() { ListServices = "" });
        var services = listed.ListServicesResponse.Service.Select(s => s.Name).ToList();
        foreach (var s in services)
            output.WriteLine(s); //captured in the writeup: the code-first command service shows up here

        // the reflection service describes itself...
        services.ShouldContain("grpc.reflection.v1alpha.ServerReflection");

        // ...AND the code-first command handler is discoverable (this is what FE's messagepack handlers cannot do)
        var echo = services.Where(s => s.Contains("Echo")).ShouldHaveSingleItem();

        // describing it returns a real FileDescriptorProto - a grpcurl `describe` succeeds here (unlike a FE command)
        var described = await SingleAsync(client, new() { FileContainingSymbol = echo });
        described.MessageResponseCase.ShouldBe(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse);
        described.FileDescriptorResponse.FileDescriptorProto.Count.ShouldBeGreaterThan(0);
    }

    static async Task<WebApplication> StartServerAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); //in-memory transport, no ports - grpc-dotnet works over the test handler

        builder.Services.AddCodeFirstGrpc();           //protobuf-net.Grpc.AspNetCore
        builder.Services.AddCodeFirstGrpcReflection();  //protobuf-net.Grpc.AspNetCore.Reflection

        var app = builder.Build();
        app.MapGrpcService<EchoCommandService>();
        app.MapCodeFirstGrpcReflectionService(); //must be mapped after the services it reflects over
        await app.StartAsync();

        return app;
    }

    static ServerReflection.ServerReflectionClient ReflectionClient(WebApplication server)
    {
        var channel = GrpcChannel.ForAddress(
            "http://localhost", //ignored - the in-memory test handler does the transport
            new GrpcChannelOptions { HttpHandler = server.GetTestServer().CreateHandler() });

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
}

// a FE command re-expressed as a code-first protobuf service. in FE this is `EchoCommand : ICommand<EchoCommand>`;
// here the same shape becomes a [ServiceContract] with field-numbered messages so protobuf-net can build a schema.
[Service("TestCases.CommandBusTest.EchoCommand")] //protobuf-net.Grpc's own contract attribute; no System.ServiceModel dependency
public interface IEchoCommandService
{
    ValueTask<EchoReply> ExecuteAsync(EchoRequest request);
}

sealed class EchoCommandService : IEchoCommandService
{
    public ValueTask<EchoReply> ExecuteAsync(EchoRequest request)
        => new(new EchoReply { FirstName = request.FirstName, LastName = request.LastName });
}

[DataContract]
public class EchoRequest
{
    [DataMember(Order = 1)] public string FirstName { get; set; } = "";
    [DataMember(Order = 2)] public string LastName { get; set; } = "";
}

[DataContract]
public class EchoReply
{
    [DataMember(Order = 1)] public string FirstName { get; set; } = "";
    [DataMember(Order = 2)] public string LastName { get; set; } = "";
}
