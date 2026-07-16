namespace PlayGroundSharp.Web.Client;

public sealed record WebSessionCreated(Guid SessionId, IReadOnlyList<string> Usings);

public sealed record WebUsingRequest(string Namespace);
