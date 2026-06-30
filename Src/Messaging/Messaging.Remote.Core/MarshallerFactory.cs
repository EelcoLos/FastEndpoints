using Grpc.Core;

namespace FastEndpoints;

/// <summary>
/// creates the grpc <see cref="Marshaller{T}" /> used to (de)serialize commands and results on the wire.
/// swap <see cref="RemoteMarshaller.Factory" /> to change the wire format (e.g. protobuf for cross-ecosystem interop).
/// </summary>
public interface IMarshallerFactory
{
    /// <summary>
    /// creates a marshaller for the given command/result type.
    /// </summary>
    /// <typeparam name="T">the command or result type being marshalled</typeparam>
    Marshaller<T> Create<T>() where T : class;
}

sealed class MessagePackMarshallerFactory : IMarshallerFactory
{
    internal static readonly MessagePackMarshallerFactory Instance = new();

    public Marshaller<T> Create<T>() where T : class
        => new MessagePackMarshaller<T>();
}

/// <summary>
/// the wire-format marshaller used by remote command handlers and clients. defaults to messagepack.
/// </summary>
public static class RemoteMarshaller
{
    // ponytail: global hook keeps the change minimal; the upgrade path is a per-connection (RemoteConnection)
    // and per-server (GrpcServiceOptions) option once a pluggable wire format becomes a shipped opt-in feature.
    /// <summary>
    /// the factory that produces the grpc marshallers for all remote commands/results. defaults to messagepack.
    /// </summary>
    public static IMarshallerFactory Factory { get; set; } = MessagePackMarshallerFactory.Instance;
}
