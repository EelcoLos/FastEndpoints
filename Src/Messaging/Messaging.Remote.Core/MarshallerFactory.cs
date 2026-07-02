using Grpc.Core;

namespace FastEndpoints;

/// <summary>
/// creates the grpc <see cref="Marshaller{T}" /> used to (de)serialize commands, results and events on the wire.
/// register a custom implementation to change the wire format (e.g. protobuf for cross-ecosystem interop) via the
/// <c>marshaller</c> argument of <c>AddHandlerServer()</c> on the server, or via <c>RemoteConnection.MarshallerFactory</c> on the client.
/// defaults to messagepack.
/// </summary>
public interface IRpcMarshallerFactory
{
    /// <summary>
    /// creates a marshaller for the given command/result/event type.
    /// </summary>
    /// <typeparam name="T">the type being marshalled</typeparam>
    Marshaller<T> Create<T>() where T : class;
}

sealed class MessagePackMarshallerFactory : IRpcMarshallerFactory
{
    internal static readonly MessagePackMarshallerFactory Instance = new();

    public Marshaller<T> Create<T>() where T : class
        => new MessagePackMarshaller<T>();
}
