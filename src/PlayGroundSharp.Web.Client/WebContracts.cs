namespace PlayGroundSharp.Web.Client;

using PlayGroundSharp.Core;

public sealed record WebSessionCreated(Guid SessionId, IReadOnlyList<string> Usings);

public sealed record WebUsingRequest(string Namespace);

public sealed record WebCompletionRequest(string Code, int Position);

public sealed record WebCompletionItem(
    string DisplayText,
    string FilterText,
    string SortText,
    IReadOnlyList<string> Tags,
    string TextToInsert,
    string? RequiredNamespace,
    int? ReplacementStart,
    string? NamespaceHint,
    bool IsExtensionMethod);

public sealed record WebCompletionDescriptionRequest(string Code, int Position, WebCompletionItem Item);

public sealed record WebSignatureParameter(string Name, string TypeName, string Summary);

public sealed record WebSignature(
    string DisplayText,
    string Summary,
    IReadOnlyList<WebSignatureParameter> Parameters,
    int ActiveParameter);

public sealed record WebSignatureHelp(IReadOnlyList<WebSignature> Signatures, int SelectedSignature);

public sealed record WebFileUploaded(
    string FileName,
    string ServerPath,
    IReadOnlyList<PipeEnvelope> Events);

public sealed record WebEditorEdit(string Value, int Caret);
