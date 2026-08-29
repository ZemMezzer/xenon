using System.Collections.Immutable;
using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

public enum LanguageServerLifecycleState
{
    Uninitialized,
    InitializeResponded,
    Initialized,
    ShutdownRequested,
    Exited,
}

public sealed class LanguageServerSession : IAsyncDisposable
{
    private readonly DocumentContextResolver _resolver = new();
    private readonly LanguageServerAnalysisContextFactory _analysisContexts;
    private readonly List<Xenon.ProjectSystem.Workspace> _workspaces = [];
    private readonly Dictionary<string, string> _openUris = new(DocumentUri.PathComparer);
    private readonly DiagnosticScheduler _diagnostics;
    private readonly TextWriter _log;
    private bool _disposed;

    public LanguageServerSession(Func<string, object?, Task> sendNotification, TextWriter? log = null,
        TimeSpan? diagnosticDebounce = null)
    {
        _log = log ?? TextWriter.Null;
        _analysisContexts = new LanguageServerAnalysisContextFactory(_resolver);
        _diagnostics = new DiagnosticScheduler(_analysisContexts,
            _ => Task.FromResult<object?>(Array.Empty<object>()),
            (uri, result) => sendNotification("textDocument/publishDiagnostics",
                new { uri, version = LspDocumentVersions.ToLsp(result.Version),
                    diagnostics = Array.Empty<object>() }),
            diagnosticDebounce);
    }

    public LanguageServerLifecycleState State { get; private set; }
    public int ExitCode { get; private set; } = 1;
    public IReadOnlyList<Xenon.ProjectSystem.Workspace> Workspaces => _workspaces;

    /// <summary>
    /// Common execution boundary for future semantic handlers. It captures one stale-sensitive
    /// analysis context and maps only server-owned cancellation sources to RequestCancelled.
    /// </summary>
    public async Task<object?> ExecuteSemanticRequestAsync(string uri,
        Func<LanguageServerAnalysisContext, Task<object?>> handler,
        CancellationToken requestCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureRunning();
        Xenon.ProjectSystem.Workspace workspace = _workspaces.FirstOrDefault(candidate =>
            !_resolver.ResolveAll(candidate.CurrentSnapshot, uri).IsEmpty) ??
            throw InvalidParams($"Document '{uri}' is not part of an initialized Workspace.");
        LanguageServerAnalysisContext? context = null;
        try
        {
            context = _analysisContexts.Create(workspace, uri, staleSensitive: true,
                requestCancellation);
            object? result = await handler(context).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (
            requestCancellation.IsCancellationRequested ||
            context?.CancellationToken.IsCancellationRequested == true)
        {
            throw new JsonRpcException(LspErrorCodes.RequestCancelled, "Request cancelled.");
        }
        finally
        {
            context?.Dispose();
        }
    }

    public Task<object?> HandleRequestAsync(string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "initialize" => Task.FromResult(Initialize(parameters, cancellationToken)),
            "shutdown" => Task.FromResult(Shutdown()),
            _ when State == LanguageServerLifecycleState.Uninitialized =>
                throw new JsonRpcException(LspErrorCodes.ServerNotInitialized,
                    "The language server has not been initialized."),
            _ when State == LanguageServerLifecycleState.ShutdownRequested =>
                throw new JsonRpcException(LspErrorCodes.InvalidRequest,
                    "The language server is shutting down."),
            _ => throw new JsonRpcException(LspErrorCodes.MethodNotFound,
                $"Method '{method}' is not supported."),
        };
    }

    public async Task HandleNotificationAsync(string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (method)
        {
            case "initialized":
                if (State == LanguageServerLifecycleState.InitializeResponded)
                    State = LanguageServerLifecycleState.Initialized;
                else await _log.WriteLineAsync($"Ignoring invalid 'initialized' transition from {State}.");
                break;
            case "exit":
                ExitCode = State == LanguageServerLifecycleState.ShutdownRequested ? 0 : 1;
                State = LanguageServerLifecycleState.Exited;
                break;
            case "textDocument/didOpen":
                EnsureRunning();
                DidOpen(RequireParams(parameters), cancellationToken);
                break;
            case "textDocument/didChange":
                EnsureRunning();
                DidChange(RequireParams(parameters), cancellationToken);
                break;
            case "textDocument/didSave":
                EnsureRunning();
                DidSave(RequireParams(parameters), cancellationToken);
                break;
            case "textDocument/didClose":
                EnsureRunning();
                DidClose(RequireParams(parameters), cancellationToken);
                break;
        }
    }

    private object? Initialize(JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (State != LanguageServerLifecycleState.Uninitialized)
            throw new JsonRpcException(LspErrorCodes.InvalidRequest, "Initialize may only be requested once.");
        JsonElement value = RequireParams(parameters);
        string? rootUri = GetOptionalString(value, "rootUri");
        string? rootPath = GetOptionalString(value, "rootPath");
        string? explicitPath = null;
        if (value.TryGetProperty("initializationOptions", out JsonElement options) &&
            options.ValueKind == JsonValueKind.Object)
            explicitPath = GetOptionalString(options, "workspacePath") ??
                GetOptionalString(options, "projectPath");

        WorkspaceDiscoveryResult discovery = WorkspaceDiscovery.Discover(explicitPath, rootUri,
            rootPath, cancellationToken);
        if (discovery.Workspace is not null) _workspaces.Add(discovery.Workspace);
        State = LanguageServerLifecycleState.InitializeResponded;
        return new
        {
            capabilities = ServerCapabilities.Create(),
            serverInfo = new { name = "Xenon Language Server", version = "0.1.0-dev" },
        };
    }

    private object? Shutdown()
    {
        if (State == LanguageServerLifecycleState.Uninitialized)
            throw new JsonRpcException(LspErrorCodes.ServerNotInitialized,
                "Shutdown cannot be requested before initialize.");
        if (State is LanguageServerLifecycleState.ShutdownRequested or LanguageServerLifecycleState.Exited)
            throw new JsonRpcException(LspErrorCodes.InvalidRequest, "Shutdown was already requested.");
        State = LanguageServerLifecycleState.ShutdownRequested;
        return null;
    }

    private void DidOpen(JsonElement parameters, CancellationToken cancellationToken)
    {
        JsonElement document = RequireObject(parameters, "textDocument");
        string uri = RequireString(document, "uri");
        string text = RequireString(document, "text");
        DocumentVersion version = ReadVersion(document);
        List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts =
            ResolveContexts(uri);
        if (contexts.Count == 0)
        {
            string path = DocumentUri.ToNormalizedPath(uri);
            var loose = WorkspaceDiscovery.CreateLooseFile(path, cancellationToken);
            _workspaces.Add(loose);
            contexts = ResolveContexts(uri);
        }
        ValidateOpen(contexts);
        foreach (var item in contexts)
            item.Workspace.OpenDocument(item.Context.DocumentId, text, version, cancellationToken);
        TrackAndSchedule(uri, contexts.Select(item => item.Workspace));
    }

    private void DidChange(JsonElement parameters, CancellationToken cancellationToken)
    {
        JsonElement identifier = RequireObject(parameters, "textDocument");
        string uri = RequireString(identifier, "uri");
        DocumentVersion newVersion = ReadVersion(identifier);
        if (!parameters.TryGetProperty("contentChanges", out JsonElement contentChanges) ||
            contentChanges.ValueKind != JsonValueKind.Array || contentChanges.GetArrayLength() == 0)
            throw InvalidParams("contentChanges must be a non-empty array.");

        List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts =
            ResolveContexts(uri);
        if (contexts.Count == 0) throw InvalidParams($"Document '{uri}' is not in an open Workspace.");
        ValidateVersion(contexts, newVersion, requireOpen: true);

        var prepared = new List<(Xenon.ProjectSystem.Workspace Workspace, DocumentSnapshot Before, string Text)>();
        foreach (var item in contexts)
        {
            DocumentSnapshot before = item.Workspace.CurrentSnapshot.GetDocument(item.Context.DocumentId);
            string finalText = ApplyContentChanges(before.EffectiveText, contentChanges);
            prepared.Add((item.Workspace, before, finalText));
        }
        foreach (var item in prepared)
        {
            var replacement = new DocumentTextChange(new TextSpan(0, item.Before.EffectiveText.Length), item.Text);
            item.Workspace.ApplyDocumentChanges(item.Before.Id, item.Before.Version, newVersion,
                [replacement], cancellationToken);
        }
        TrackAndSchedule(uri, contexts.Select(item => item.Workspace));
    }

    private void DidSave(JsonElement parameters, CancellationToken cancellationToken)
    {
        JsonElement identifier = RequireObject(parameters, "textDocument");
        string uri = RequireString(identifier, "uri");
        List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts =
            ResolveContexts(uri);
        foreach (IGrouping<Xenon.ProjectSystem.Workspace,
                     (Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> group in
                 contexts.GroupBy(item => item.Workspace))
        {
            DocumentContext primary = group.OrderByDescending(item => item.Context.IsRootProject)
                .ThenBy(item => item.Context.DocumentId).First().Context;
            DocumentSnapshot document = group.Key.CurrentSnapshot.GetDocument(primary.DocumentId);
            group.Key.ReloadFromDisk(document.Id, document.Version, cancellationToken);
        }
        TrackAndSchedule(uri, contexts.Select(item => item.Workspace));
    }

    private void DidClose(JsonElement parameters, CancellationToken cancellationToken)
    {
        JsonElement identifier = RequireObject(parameters, "textDocument");
        string uri = RequireString(identifier, "uri");
        List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts =
            ResolveContexts(uri);
        foreach (var item in contexts)
        {
            DocumentSnapshot document = item.Workspace.CurrentSnapshot.GetDocument(item.Context.DocumentId);
            if (document.IsOpen)
                item.Workspace.CloseDocument(document.Id, document.Version, cancellationToken);
        }
        foreach (Xenon.ProjectSystem.Workspace workspace in contexts.Select(item => item.Workspace).Distinct())
            _diagnostics.Cancel(workspace, uri);
        _openUris.Remove(DocumentUri.ToNormalizedPath(uri));
    }

    private void TrackAndSchedule(string uri, IEnumerable<Xenon.ProjectSystem.Workspace> changed)
    {
        _openUris[DocumentUri.ToNormalizedPath(uri)] = uri;
        foreach (Xenon.ProjectSystem.Workspace workspace in changed.Distinct())
        {
            IEnumerable<string> affected = _openUris.Values.Where(openUri =>
                !_resolver.ResolveAll(workspace.CurrentSnapshot, openUri).IsEmpty);
            _diagnostics.ScheduleMany(workspace, affected);
        }
    }

    private List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> ResolveContexts(string uri)
    {
        var result = new List<(Xenon.ProjectSystem.Workspace, DocumentContext)>();
        foreach (Xenon.ProjectSystem.Workspace workspace in _workspaces)
            result.AddRange(_resolver.ResolveAll(workspace.CurrentSnapshot, uri)
                .Select(context => (workspace, context)));
        return result;
    }

    private static string ApplyContentChanges(SourceText source, JsonElement changes)
    {
        SourceText current = source;
        foreach (JsonElement change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object) throw InvalidParams("A content change must be an object.");
            string replacement = RequireString(change, "text");
            if (!change.TryGetProperty("range", out JsonElement rangeElement) ||
                rangeElement.ValueKind == JsonValueKind.Null)
            {
                current = current.WithText(replacement);
                continue;
            }
            LspRange range = ReadRange(rangeElement);
            TextSpan span;
            try { span = LspTextCoordinates.ToTextSpan(current, range); }
            catch (ArgumentOutOfRangeException exception) { throw InvalidParams(exception.Message); }
            if (change.TryGetProperty("rangeLength", out JsonElement length) &&
                length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out int supplied) &&
                supplied != span.Length)
                throw InvalidParams("rangeLength does not match the UTF-16 range length.");
            string text = string.Concat(current.Text.AsSpan(0, span.Start), replacement,
                current.Text.AsSpan(span.End));
            current = current.WithText(text);
        }
        return current.Text;
    }

    private static LspRange ReadRange(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidParams("range must be an object.");
        return new LspRange(ReadPosition(RequireObject(value, "start")),
            ReadPosition(RequireObject(value, "end")));
    }

    private static LspPosition ReadPosition(JsonElement value) =>
        new(RequireInt32(value, "line"), RequireInt32(value, "character"));

    private static void ValidateVersion(
        IEnumerable<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts,
        DocumentVersion version, bool requireOpen)
    {
        foreach (var item in contexts)
        {
            DocumentSnapshot document = item.Workspace.CurrentSnapshot.GetDocument(item.Context.DocumentId);
            if (requireOpen && !document.IsOpen)
                throw InvalidParams($"Document '{document.Id}' is not open.");
            if (version <= document.Version)
                throw InvalidParams($"Document version {version} is stale; current version is {document.Version}.");
        }
    }

    private static void ValidateOpen(
        IEnumerable<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> contexts)
    {
        foreach (var item in contexts)
        {
            DocumentSnapshot document = item.Workspace.CurrentSnapshot.GetDocument(item.Context.DocumentId);
            if (document.IsOpen)
                throw InvalidParams($"Document '{document.Id}' is already open.");
        }
    }

    private void EnsureRunning()
    {
        if (State != LanguageServerLifecycleState.Initialized)
            throw new JsonRpcException(LspErrorCodes.ServerNotInitialized,
                "The language server is not ready for document synchronization.");
    }

    private static JsonElement RequireParams(JsonElement? value) => value is { ValueKind: JsonValueKind.Object }
        ? value.Value : throw InvalidParams("params must be an object.");
    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Object
            ? property : throw InvalidParams($"'{name}' must be an object.");
    private static string RequireString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw InvalidParams($"'{name}' must be a string.");
    private static string? GetOptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static int RequireInt32(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.TryGetInt32(out int result)
            ? result : throw InvalidParams($"'{name}' must be a 32-bit integer.");
    private static DocumentVersion ReadVersion(JsonElement value)
    {
        int raw = RequireInt32(value, "version");
        return LspDocumentVersions.FromLsp(raw);
    }
    private static JsonRpcException InvalidParams(string message) =>
        new(LspErrorCodes.InvalidParams, message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _diagnostics.DisposeAsync().ConfigureAwait(false);
        foreach (Xenon.ProjectSystem.Workspace workspace in _workspaces) workspace.Dispose();
        _workspaces.Clear();
    }
}
