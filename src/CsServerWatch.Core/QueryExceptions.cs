namespace CsServerWatch.Core;

public class QueryError : Exception
{
    public QueryError(string userMessage) : base(userMessage)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
}

public sealed class AddressError : QueryError
{
    public AddressError(string userMessage) : base(userMessage) { }
}

public sealed class ResolveError : QueryError
{
    public ResolveError(string userMessage) : base(userMessage) { }
}

public sealed class NoResponse : QueryError
{
    public NoResponse() : base("No CS:GO or CS2 server answered at this address.") { }
}

public sealed class NotCsServer : QueryError
{
    public NotCsServer(string userMessage) : base(userMessage) { }
}

public sealed class NotAServerQuery : QueryError
{
    public NotAServerQuery()
        : base("The address answered, but not with a CS:GO or CS2 server query.") { }
}

public sealed class ProtocolError : Exception
{
    public ProtocolError(string message) : base(message) { }
}

public sealed class GoldSrcResponse : Exception
{
    public GoldSrcResponse() : base("goldsrc") { }
}
