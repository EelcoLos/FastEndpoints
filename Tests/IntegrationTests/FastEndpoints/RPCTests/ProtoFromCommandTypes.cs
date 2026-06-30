using System.Buffers;
using System.Reflection;
using Grpc.Core;
using ProtoBuf.Meta;
using TestCases.CommandBusTest;

namespace RemoteProcedureCalls;

// prototype for issue #620: the ".proto from command types" + pluggable wire format path.
//
// FE already knows every command/result CLR type at registration time. this shows we can:
//   1. generate a real protobuf .proto straight from those types - no .proto authoring, no attributes
//   2. carry the same types over a protobuf Marshaller<T> (the pluggable wire-format seam)
//
// with a protobuf wire format + generated descriptors, grpc reflection and every-language codegen
// come from the standard protoc/buf ecosystem for free, instead of FE owning a multi-language generator.
public class ProtoFromCommandTypes(ITestOutputHelper output)
{
    [Fact]
    public void Generates_Proto_From_A_Command_Type_With_No_Attributes()
    {
        //EchoCommand is the clean case: message-in, message-out.
        var proto = ContractlessModel.For(typeof(EchoCommand)).GetSchema(typeof(EchoCommand), ProtoSyntax.Proto3);

        output.WriteLine(proto); //the generated .proto, captured in the writeup

        proto.ShouldContain("syntax = \"proto3\"");
        proto.ShouldContain("message EchoCommand");
        proto.ShouldContain("FirstName");
        proto.ShouldContain("LastName");
    }

    [Fact]
    public void Protobuf_Marshaller_RoundTrips_A_Real_FE_Command()
    {
        var cmd = new EchoCommand { FirstName = "johnny", LastName = "lawrence" };

        // go through the IMarshallerFactory seam - this is exactly the type you'd assign to
        // RemoteMarshaller.Factory to flip the wire format for both client and server.
        IMarshallerFactory factory = new ProtobufMarshallerFactory();
        var roundTripped = RoundTrip(factory.Create<EchoCommand>(), cmd);

        roundTripped.FirstName.ShouldBe("johnny");
        roundTripped.LastName.ShouldBe("lawrence");
    }

    [Fact]
    public void Default_Wire_Format_Is_Unchanged()
        // the seam defaults to messagepack, so swapping it in is opt-in and existing servers/clients are untouched.
        => RemoteMarshaller.Factory.Create<EchoCommand>().ShouldBeAssignableTo<Marshaller<EchoCommand>>();

    [Fact]
    public void Primitive_Result_Needs_A_Wrapper()
    {
        //SomeCommand : ICommand<string>. a gRPC method's request/response must be a *message* type, but
        //protobuf has no top-level scalar - so a bare `string` result can't map the way messagepack allows.
        //this is the one wrinkle of the protobuf path: primitive results must be wrapped in a one-field
        //message (protobuf-net.Grpc does this automatically with its well-known wrappers).
        var proto = ContractlessModel.For(typeof(StringValue)).GetSchema(typeof(StringValue), ProtoSyntax.Proto3);
        proto.ShouldContain("message StringValue");

        RoundTrip(new ProtobufMarshallerFactory().Create<StringValue>(), new() { Value = "johnny lawrence" }).Value.ShouldBe("johnny lawrence");
    }

    //drives a Marshaller<T> exactly how grpc-dotnet would: serialize via the buffer writer, read back from the payload.
    static T RoundTrip<T>(Marshaller<T> m, T value)
    {
        var sc = new TestSerializationContext();
        m.ContextualSerializer(value, sc);

        return m.ContextualDeserializer(new TestDeserializationContext(sc.Payload));
    }

    class StringValue
    {
        public string Value { get; set; } = "";
    }

    sealed class TestSerializationContext : SerializationContext
    {
        readonly ArrayBufferWriter<byte> _writer = new();
        public ReadOnlySequence<byte> Payload => new(_writer.WrittenMemory);
        public override IBufferWriter<byte> GetBufferWriter() => _writer;
        public override void Complete() { }
        public override void Complete(byte[] payload) { }
    }

    sealed class TestDeserializationContext(ReadOnlySequence<byte> payload) : DeserializationContext
    {
        public override int PayloadLength => (int)payload.Length;
        public override ReadOnlySequence<byte> PayloadAsReadOnlySequence() => payload;
        public override byte[] PayloadAsNewBuffer() => payload.ToArray();
    }
}

// the wire-format swap target: assign `RemoteMarshaller.Factory = new ProtobufMarshallerFactory()` to put
// FE's remote commands/results on a protobuf wire instead of messagepack.
sealed class ProtobufMarshallerFactory : IMarshallerFactory
{
    public Marshaller<T> Create<T>() where T : class
        => new ProtobufMarshaller<T>();
}

// protobuf counterpart of FE's MsgPackMarshaller - same shape, protobuf wire format instead of messagepack.
sealed class ProtobufMarshaller<T>() : Marshaller<T>(Serialize, Deserialize) where T : class
{
    static readonly RuntimeTypeModel _model = ContractlessModel.For(typeof(T));

    public static T Deserialize(DeserializationContext ctx)
        => _model.Deserialize<T>(ctx.PayloadAsReadOnlySequence());

    public static void Serialize(T value, SerializationContext ctx)
    {
        _model.Serialize(ctx.GetBufferWriter(), value);
        ctx.Complete();
    }
}

// builds a protobuf model that mirrors FE's MessagePack ContractlessStandardResolver: no [ProtoContract] needed.
// replicates ImplicitFields.AllPublic - public members, alphabetical, numbered from 1.
static class ContractlessModel
{
    public static RuntimeTypeModel For(params Type[] types)
    {
        var model = RuntimeTypeModel.Create();

        foreach (var t in types)
        {
            var meta = model.Add(t, applyDefaultBehaviour: false);
            var field = 1;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
                meta.Add(field++, p.Name);
        }

        return model;
    }
}
