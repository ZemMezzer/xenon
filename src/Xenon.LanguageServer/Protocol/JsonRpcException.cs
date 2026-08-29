namespace Xenon.LanguageServer.Protocol;

public sealed class JsonRpcException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public object? DataObject { get; } = data;
}
