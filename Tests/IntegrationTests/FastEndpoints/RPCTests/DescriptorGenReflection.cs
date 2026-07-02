using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Net.Client;
using Grpc.Reflection;
using Grpc.Reflection.V1Alpha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TestCases.CommandBusTest;
using ServiceDescriptor = Google.Protobuf.Reflection.ServiceDescriptor; //disambiguate from Microsoft.Extensions.DependencyInjection.ServiceDescriptor

namespace RemoteProcedureCalls;

// prototype for issue #620, PATH A of the descriptor bake-off: FE generates the descriptors itself.
//
// a Google.Protobuf ServiceDescriptor is built at runtime straight from a command's CLR types (same
// contractless shape FE's MessagePack resolver uses - no attributes, no hand-authored .proto), then handed
// to the stock Grpc.Reflection service. grpc reflection then lists + describes the command handler under the
// exact service name FE binds it to. handlers stay attribute-free ICommand<T>; FE owns the generation code.
//
// note: hand-building with Google.Protobuf's own descriptor types avoids the protobuf-net.Reflection vs
// Google.Protobuf `Google.Protobuf.Reflection.*` namespace collision entirely (no extern alias needed).
public class DescriptorGenReflection(ITestOutputHelper output)
{
    [Fact]
    public void Generates_A_Real_ServiceDescriptor_From_A_Command_Type()
    {
        var file = CommandDescriptorFactory.BuildFile(typeof(EchoCommand), typeof(EchoCommand));

        var svc = file.Services.ShouldHaveSingleItem();
        svc.FullName.ShouldBe("TestCases.CommandBusTest.EchoCommand"); //the same service name FE binds the handler under

        var method = svc.Methods.ShouldHaveSingleItem();

        //the request message mirrors the contractless MessagePack shape: public props, alphabetical, from field 1
        method.InputType.Fields.InFieldNumberOrder().Select(f => f.Name).ShouldBe(["FirstName", "LastName"]);
    }

    [Fact]
    public async Task Reflection_Lists_And_Describes_A_Handler_From_Generated_Descriptors()
    {
        var file = CommandDescriptorFactory.BuildFile(typeof(EchoCommand), typeof(EchoCommand));

        await using var server = await StartReflectionServerAsync(file.Services);
        var client = ReflectionClient(server);

        var listed = await SingleAsync(client, new() { ListServices = "" });
        var services = listed.ListServicesResponse.Service.Select(s => s.Name).ToList();
        foreach (var s in services)
            output.WriteLine(s); //captured in the writeup: the FE command now shows up alongside the reflection service

        //the manually-seeded reflection service lists exactly the descriptors we gave it (it doesn't advertise
        //itself, unlike AddGrpcReflection's auto-discovery) - and the FE command is now among them.
        services.ShouldContain("TestCases.CommandBusTest.EchoCommand"); //the FE command, now visible to reflection

        //describing it returns a real FileDescriptorProto - a grpcurl `describe` succeeds against a FE command
        var described = await SingleAsync(client, new() { FileContainingSymbol = "TestCases.CommandBusTest.EchoCommand" });
        described.MessageResponseCase.ShouldBe(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse);
        described.FileDescriptorResponse.FileDescriptorProto.Count.ShouldBeGreaterThan(0);
    }

    static async Task<WebApplication> StartReflectionServerAsync(IList<ServiceDescriptor> descriptors)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(); //in-memory transport, no ports

        builder.Services.AddGrpc();

        //seed the stock grpc reflection service with descriptors we generated, instead of AddGrpcReflection()'s
        //endpoint auto-discovery (which finds no descriptor for FE's dynamically-bound messagepack methods).
        //map both v1 and v1alpha - modern grpcurl asks for v1 first, older clients for v1alpha.
        builder.Services.AddSingleton(new ReflectionServiceImpl(descriptors));
        builder.Services.AddSingleton(new ReflectionV1ServiceImpl(descriptors));

        var app = builder.Build();
        app.MapGrpcService<ReflectionServiceImpl>();
        app.MapGrpcService<ReflectionV1ServiceImpl>();
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

// builds a self-contained proto3 FileDescriptor for a command: a service named after the command's FullName
// (matching how FE binds the handler) plus the request/result message types, generated straight from the CLR
// types with the same contractless shape as FE's MessagePack ContractlessStandardResolver.
static class CommandDescriptorFactory
{
    public static FileDescriptor BuildFile(Type commandType, Type resultType)
    {
        var package = commandType.Namespace ?? "";

        var fdp = new FileDescriptorProto
        {
            Name = $"{commandType.FullName}.proto",
            Package = package,
            Syntax = "proto3"
        };

        //one message per distinct CLR type, name suffixed so a message never collides with the same-named service
        var messageNames = new Dictionary<Type, string>();
        foreach (var t in new[] { commandType, resultType }.Distinct())
        {
            var msgName = $"{t.Name}Msg";
            messageNames[t] = msgName;
            fdp.MessageType.Add(BuildMessage(t, msgName));
        }

        var service = new ServiceDescriptorProto { Name = commandType.Name }; //service FQN = package.Name = command FullName
        service.Method.Add(
            new MethodDescriptorProto
            {
                Name = "Execute",
                InputType = $".{package}.{messageNames[commandType]}",  //leading dot + fully-qualified
                OutputType = $".{package}.{messageNames[resultType]}"
            });
        fdp.Service.Add(service);

        return FileDescriptor.BuildFromByteStrings([fdp.ToByteString()])[0];
    }

    //mirror MessagePack's ImplicitFields.AllPublic: public instance props, alphabetical, numbered from 1
    static DescriptorProto BuildMessage(Type t, string name)
    {
        var msg = new DescriptorProto { Name = name };
        var field = 1;

        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            msg.Field.Add(
                new FieldDescriptorProto
                {
                    Name = p.Name,
                    Number = field++,
                    Label = FieldDescriptorProto.Types.Label.Optional, //proto3 singular
                    Type = ScalarType(p.PropertyType)
                });
        }

        return msg;
    }

    static FieldDescriptorProto.Types.Type ScalarType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;

        return t switch
        {
            not null when t == typeof(string) => FieldDescriptorProto.Types.Type.String,
            not null when t == typeof(bool) => FieldDescriptorProto.Types.Type.Bool,
            not null when t == typeof(int) || t == typeof(short) || t == typeof(sbyte) => FieldDescriptorProto.Types.Type.Int32,
            not null when t == typeof(long) => FieldDescriptorProto.Types.Type.Int64,
            not null when t == typeof(uint) || t == typeof(ushort) || t == typeof(byte) => FieldDescriptorProto.Types.Type.Uint32,
            not null when t == typeof(ulong) => FieldDescriptorProto.Types.Type.Uint64,
            not null when t == typeof(double) => FieldDescriptorProto.Types.Type.Double,
            not null when t == typeof(float) => FieldDescriptorProto.Types.Type.Float,

            // ponytail: prototype covers scalar fields (enough for EchoCommand + the common case). nested message
            // fields and collections are the next increment - emit a message per nested type and set Type.Message + TypeName.
            _ => throw new NotSupportedException($"prototype descriptor-gen handles scalar fields only; property type [{t}] is not yet mapped")
        };
    }
}
