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

public sealed class LanguageServerRuntimeHooks
{
    public Action? BeforeAnalysisAcquisition { get; init; }
    public Func<CancellationToken, Task>? BeforeReloadCommitAsync { get; init; }
    public Func<CancellationToken, Task>? AfterDiagnosticAnalysisAsync { get; init; }
    public Action<IReadOnlyList<Xenon.ProjectSystem.Workspace>>? ReloadCandidatesPrepared { get; init; }
}

public sealed class LanguageServerSession : IAsyncDisposable
{
    private readonly object _publicationGate = new();
    private readonly DocumentContextResolver _resolver = new();
    private readonly LanguageServerAnalysisContextFactory _analysisContexts;
    private Workspace[] _workspaces = [];
    private readonly Dictionary<string, string> _openUris = new(DocumentUri.PathComparer);
    private readonly DiagnosticScheduler _diagnostics;
    private readonly Func<string, object?, Task> _sendNotification;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly SemaphoreSlim _diagnosticPublicationGate = new(1, 1);
    private readonly LanguageServerRuntimeHooks? _runtimeHooks;
    private Xenon.ProjectSystem.Workspace? _primaryWorkspace;
    private string? _configurationPath;
    private string? _workspaceDiscoveryRoot;
    private int _knownUriCount;
    private long _workspaceSetGeneration;
    private long _reloadSequence;
    private bool _pendingSourceReconciliation;
    private bool _pendingConfigurationReconciliation;
    private bool _disposed;

    public LanguageServerSession(Func<string, object?, Task> sendNotification, TextWriter? log = null,
        TimeSpan? diagnosticDebounce = null, LanguageServerRuntimeHooks? runtimeHooks = null)
    {
        _log = log ?? TextWriter.Null;
        _sendNotification = sendNotification;
        _runtimeHooks = runtimeHooks;
        _analysisContexts = new LanguageServerAnalysisContextFactory(_resolver);
        _diagnostics = new DiagnosticScheduler(_analysisContexts,
            AnalyzeDiagnosticsAsync,
            (uri, result) => sendNotification("textDocument/publishDiagnostics",
                new
                {
                    uri,
                    version = LspDocumentVersions.ToLsp(result.Version),
                    diagnostics = result.Value ?? Array.Empty<object>()
                }),
            diagnosticDebounce, AcquireDiagnosticContext, CanPublishDiagnostics,
            PublishDiagnosticsIfCurrentAsync);
    }

    public LanguageServerLifecycleState State { get; private set; }
    public int ExitCode { get; private set; } = 1;
    public IReadOnlyList<Xenon.ProjectSystem.Workspace> Workspaces => Volatile.Read(ref _workspaces);
    public int KnownUriCount => Volatile.Read(ref _knownUriCount);
    public int PendingDiagnosticCount => _diagnostics.InFlightJobCount;
    public bool PendingConfigurationReconciliation =>
        Volatile.Read(ref _pendingConfigurationReconciliation);

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
        LanguageServerAnalysisContext? context = null;
        try
        {
            lock (_publicationGate)
            {
                _runtimeHooks?.BeforeAnalysisAcquisition?.Invoke();
                Workspace[] workspaces = _workspaces;
                Xenon.ProjectSystem.Workspace workspace = workspaces.FirstOrDefault(candidate =>
                    !_resolver.ResolveAll(candidate.CurrentSnapshot, uri).IsEmpty) ??
                    throw InvalidParams($"Document '{uri}' is not part of an initialized Workspace.");
                context = _analysisContexts.Create(workspace, uri, staleSensitive: true,
                    requestCancellation);
            }
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
            "workspace/symbol" => ExecuteWorkspaceSymbolRequestAsync(RequireParams(parameters), cancellationToken),
            "textDocument/hover" or "textDocument/definition" or "textDocument/typeDefinition" or
            "textDocument/references" or "textDocument/implementation" or
            "textDocument/documentSymbol" or "textDocument/completion" or
            "textDocument/signatureHelp" or "textDocument/semanticTokens/full" or
            "textDocument/prepareRename" or "textDocument/rename" =>
                ExecuteCoreDocumentRequestAsync(method, RequireParams(parameters), cancellationToken),
            _ => throw new JsonRpcException(LspErrorCodes.MethodNotFound,
                $"Method '{method}' is not supported."),
        };
    }

    private Task<object?> ExecuteCoreDocumentRequestAsync(string method, JsonElement parameters,
        CancellationToken cancellationToken)
    {
        string uri = RequireString(RequireObject(parameters, "textDocument"), "uri");
        return ExecuteSemanticRequestAsync(uri,
            context => LspCoreIntelligence.HandleDocumentRequestAsync(context, method, parameters),
            cancellationToken);
    }

    private async Task<object?> ExecuteWorkspaceSymbolRequestAsync(JsonElement parameters,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        string query = RequireString(parameters, "query");
        WorkspaceAnalysisRequest request;
        lock (_publicationGate)
        {
            _runtimeHooks?.BeforeAnalysisAcquisition?.Invoke();
            if (_workspaces.Length == 0) return Array.Empty<object>();
            request = _workspaces[0].CreateAnalysisRequest(staleSensitive: true,
                cancellationToken);
        }
        using (request)
        {
            try
            {
                object? result = await LspCoreIntelligence.WorkspaceSymbolsAsync(request.Snapshot, query,
                    request.CancellationToken).ConfigureAwait(false);
                request.CancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested ||
                request.CancellationToken.IsCancellationRequested)
            {
                throw new JsonRpcException(LspErrorCodes.RequestCancelled, "Request cancelled.");
            }
        }
    }

    private async Task<object?> AnalyzeDiagnosticsAsync(LanguageServerAnalysisContext context)
    {
        object? result = await LspCoreIntelligence.DiagnosticsAsync(context).ConfigureAwait(false);
        if (_runtimeHooks?.AfterDiagnosticAnalysisAsync is { } hook)
            await hook(context.CancellationToken).ConfigureAwait(false);
        return result;
    }

    private LanguageServerAnalysisContext? AcquireDiagnosticContext(
        Xenon.ProjectSystem.Workspace workspace, string uri, long workspaceSetGeneration,
        CancellationToken cancellationToken)
    {
        lock (_publicationGate)
        {
            if (_workspaceSetGeneration != workspaceSetGeneration ||
                !_workspaces.Contains(workspace)) return null;
            return _analysisContexts.Create(workspace, uri, staleSensitive: true,
                cancellationToken);
        }
    }

    private bool CanPublishDiagnostics(Xenon.ProjectSystem.Workspace workspace,
        long workspaceSetGeneration)
    {
        lock (_publicationGate)
            return _workspaceSetGeneration == workspaceSetGeneration &&
                _workspaces.Contains(workspace);
    }

    private async Task<bool> PublishDiagnosticsIfCurrentAsync(
        Xenon.ProjectSystem.Workspace workspace, long workspaceSetGeneration, string uri,
        DiagnosticResult result)
    {
        await _diagnosticPublicationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_publicationGate)
            {
                if (_workspaceSetGeneration != workspaceSetGeneration ||
                    !_workspaces.Contains(workspace)) return false;
                WorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
                if (snapshot.Generation != result.Generation ||
                    !snapshot.TryGetDocument(result.DocumentId, out DocumentSnapshot? document) ||
                    document!.Version != result.Version)
                    return false;
            }
            await _sendNotification("textDocument/publishDiagnostics",
                new
                {
                    uri,
                    version = LspDocumentVersions.ToLsp(result.Version),
                    diagnostics = result.Value ?? Array.Empty<object>()
                }).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _diagnosticPublicationGate.Release();
        }
    }

    public async Task HandleNotificationAsync(string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                    await MutateWithDiagnosticBarrierAsync(() =>
                    {
                        DidOpen(RequireParams(parameters), cancellationToken);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didChange":
                    EnsureRunning();
                    await MutateWithDiagnosticBarrierAsync(() =>
                    {
                        DidChange(RequireParams(parameters), cancellationToken);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didSave":
                    EnsureRunning();
                    await MutateWithDiagnosticBarrierAsync(() =>
                    {
                        DidSave(RequireParams(parameters), cancellationToken);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                case "textDocument/didClose":
                    EnsureRunning();
                    await MutateWithDiagnosticBarrierAsync(() =>
                        DidCloseAsync(RequireParams(parameters), cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case "workspace/didChangeWatchedFiles":
                    EnsureRunning();
                    await DidChangeWatchedFilesAsync(RequireParams(parameters), cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task MutateWithDiagnosticBarrierAsync(Func<Task> mutation,
        CancellationToken cancellationToken)
    {
        await _diagnosticPublicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await mutation().ConfigureAwait(false); }
        finally { _diagnosticPublicationGate.Release(); }
    }

    private object? Initialize(JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (State != LanguageServerLifecycleState.Uninitialized)
            throw new JsonRpcException(LspErrorCodes.InvalidRequest, "Initialize may only be requested once.");
        JsonElement value = RequireParams(parameters);
        string? rootUri = GetOptionalString(value, "rootUri");
        string? rootPath = GetOptionalString(value, "rootPath");
        if (string.IsNullOrWhiteSpace(rootUri))
            rootUri = GetFirstWorkspaceFolderUri(value);
        string? explicitPath = null;
        if (value.TryGetProperty("initializationOptions", out JsonElement options) &&
            options.ValueKind == JsonValueKind.Object)
            explicitPath = GetOptionalString(options, "workspacePath") ??
                GetOptionalString(options, "projectPath");

        WorkspaceDiscoveryResult discovery = WorkspaceDiscovery.Discover(explicitPath, rootUri,
            rootPath, cancellationToken);
        if (discovery.Workspace is not null)
        {
            lock (_publicationGate)
            {
                _primaryWorkspace = discovery.Workspace;
                _configurationPath = discovery.IsLoose ? null : discovery.ConfigurationPath;
                _workspaceDiscoveryRoot = !discovery.IsLoose &&
                    string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(discovery.SearchRoot)
                        ? discovery.SearchRoot
                        : null;
                Volatile.Write(ref _workspaces, [discovery.Workspace]);
                _workspaceSetGeneration++;
            }
        }
        State = LanguageServerLifecycleState.InitializeResponded;
        return new
        {
            capabilities = ServerCapabilities.Create(),
            serverInfo = new { name = "Xenon Language Server", version = XenonBuildInfo.Version },
        };
    }

    private static string? GetFirstWorkspaceFolderUri(JsonElement initializeParams)
    {
        if (!initializeParams.TryGetProperty("workspaceFolders", out JsonElement folders) ||
            folders.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement folder in folders.EnumerateArray())
        {
            if (folder.ValueKind != JsonValueKind.Object) continue;
            string? uri = GetOptionalString(folder, "uri");
            if (!string.IsNullOrWhiteSpace(uri)) return uri;
        }
        return null;
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
            try
            {
                DocumentContext[] looseContexts = _resolver.ResolveAll(loose.CurrentSnapshot, uri)
                    .ToArray();
                ValidateOpen(looseContexts.Select(context => (loose, context)));
                foreach (DocumentContext context in looseContexts)
                    loose.OpenDocument(context.DocumentId, text, version, cancellationToken);
                AppendWorkspace(loose);
                contexts = looseContexts.Select(context => (loose, context)).ToList();
                loose = null!;
            }
            finally
            {
                loose?.Dispose();
            }
            TrackAndSchedule(uri, contexts.Select(item => item.Workspace));
            return;
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
        ScheduleAffectedOpenDiagnostics(contexts.Select(item => item.Workspace));
    }

    private async Task DidCloseAsync(JsonElement parameters, CancellationToken cancellationToken)
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
        Volatile.Write(ref _knownUriCount, _openUris.Count);
        await _sendNotification("textDocument/publishDiagnostics",
            new { uri, diagnostics = Array.Empty<object>() }).ConfigureAwait(false);
        PruneClosedAuxiliaryWorkspaces();
    }

    private async Task DidChangeWatchedFilesAsync(JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("changes", out JsonElement changes) ||
            changes.ValueKind != JsonValueKind.Array)
            throw InvalidParams("changes must be an array.");

        var events = new List<(string Uri, string Path, int Type)>();
        foreach (JsonElement change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object)
                throw InvalidParams("A watched file change must be an object.");
            string uri = RequireString(change, "uri");
            int type = RequireInt32(change, "type");
            if (type is < 1 or > 3) throw InvalidParams("A watched file change type must be 1, 2, or 3.");
            events.Add((uri, DocumentUri.ToNormalizedPath(uri), type));
        }

        bool hasConfigurationEvent = events.Any(item => IsConfigurationPath(item.Path));
        bool configurationChanged = events.Any(item =>
            IsRelevantConfigurationPath(item.Path) || IsDiscoveryConfigurationPath(item.Path));
        bool sourceSetMayHaveChanged = events.Any(item => item.Type is 1 or 3 &&
            Path.GetExtension(item.Path).Equals(".xe", StringComparison.OrdinalIgnoreCase));
        bool hasSourceEvent = events.Any(item =>
            Path.GetExtension(item.Path).Equals(".xe", StringComparison.OrdinalIgnoreCase));
        bool unknownChangedSource = events.Any(item => item.Type == 2 &&
            Path.GetExtension(item.Path).Equals(".xe", StringComparison.OrdinalIgnoreCase) &&
            ResolveContexts(item.Uri).Count == 0);
        if (sourceSetMayHaveChanged) _pendingSourceReconciliation = true;
        if (configurationChanged && CanReloadPrimaryWorkspace())
            _pendingConfigurationReconciliation = true;
        bool mustReconcile = configurationChanged || sourceSetMayHaveChanged ||
            unknownChangedSource || _pendingSourceReconciliation && hasSourceEvent ||
            _pendingConfigurationReconciliation && hasConfigurationEvent;
        if (mustReconcile && CanReloadPrimaryWorkspace())
        {
            bool reloaded = await TryReloadPrimaryWorkspaceAsync(cancellationToken)
                .ConfigureAwait(false);
            if (reloaded)
            {
                _pendingSourceReconciliation = false;
                _pendingConfigurationReconciliation = false;
            }
        }

        foreach (var item in events.Where(item => item.Type == 2 &&
                     File.Exists(item.Path) &&
                     Path.GetExtension(item.Path).Equals(".xe", StringComparison.OrdinalIgnoreCase)))
            await ReloadExternalSourceAsync(item.Uri, cancellationToken).ConfigureAwait(false);
    }

    private bool IsRelevantConfigurationPath(string path)
    {
        if (!IsConfigurationPath(path)) return false;
        if (_configurationPath is not null && DocumentUri.PathComparer.Equals(
                DocumentUri.NormalizePath(_configurationPath), path))
            return true;
        Xenon.ProjectSystem.Workspace? primary = _primaryWorkspace;
        return primary is not null && primary.CurrentSnapshot.Projects.Any(project =>
            project.Configuration.ProjectFilePath is { } projectPath &&
            DocumentUri.PathComparer.Equals(DocumentUri.NormalizePath(projectPath), path));
    }

    private bool IsDiscoveryConfigurationPath(string path)
    {
        if (!IsConfigurationPath(path) || _workspaceDiscoveryRoot is null) return false;
        string root = DocumentUri.NormalizePath(_workspaceDiscoveryRoot);
        string relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private bool CanReloadPrimaryWorkspace() =>
        _configurationPath is not null || _workspaceDiscoveryRoot is not null;

    private static bool IsConfigurationPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".xeproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xws", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryReloadPrimaryWorkspaceAsync(CancellationToken cancellationToken)
    {
        long sequence = Interlocked.Increment(ref _reloadSequence);
        Workspace[] published;
        Xenon.ProjectSystem.Workspace? previous;
        string? configurationPath;
        string? discoveryRoot;
        long capturedGeneration;
        lock (_publicationGate)
        {
            published = _workspaces;
            previous = _primaryWorkspace;
            configurationPath = _configurationPath;
            discoveryRoot = _workspaceDiscoveryRoot;
            capturedGeneration = _workspaceSetGeneration;
        }
        if (previous is null || configurationPath is null && discoveryRoot is null) return false;

        var unpublished = new List<Xenon.ProjectSystem.Workspace>();
        try
        {
            Dictionary<string, OpenDocumentState> overlays = CaptureOpenDocumentStates(published);
            WorkspaceDiscoveryResult? rediscovery = discoveryRoot is null ? null :
                WorkspaceDiscovery.Discover(null, null, discoveryRoot, cancellationToken);
            Xenon.ProjectSystem.Workspace candidate = rediscovery?.Workspace ??
                Xenon.ProjectSystem.Workspace.Create(configurationPath!,
                    cancellationToken: cancellationToken);
            if (candidate is null)
                throw new ProjectSystemException(
                    $"workspace root '{discoveryRoot}' did not produce a Workspace");
            unpublished.Add(candidate);

            var candidatePaths = candidate.CurrentSnapshot.Documents
                .Where(document => document.PhysicalPath is not null)
                .GroupBy(document => DocumentUri.NormalizePath(document.PhysicalPath!),
                    DocumentUri.PathComparer)
                .ToDictionary(group => group.Key, group => group.ToArray(), DocumentUri.PathComparer);
            foreach ((string path, OpenDocumentState overlay) in overlays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidatePaths.TryGetValue(path, out DocumentSnapshot[]? documents)) continue;
                foreach (DocumentSnapshot document in documents)
                    candidate.OpenDocument(document.Id, overlay.OverlayText, overlay.Version,
                        cancellationToken);
            }

            KeyValuePair<string, OpenDocumentState>[] unmatched = overlays.Where(pair =>
                !candidatePaths.ContainsKey(pair.Key)).ToArray();
            foreach ((string path, OpenDocumentState overlay) in unmatched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                unpublished.Add(Xenon.ProjectSystem.Workspace.CreateOpenLooseDocument(path,
                    overlay.OverlayText, overlay.Version, cancellationToken: cancellationToken));
            }

            _runtimeHooks?.ReloadCandidatesPrepared?.Invoke(unpublished);
            if (_runtimeHooks?.BeforeReloadCommitAsync is { } hook)
                await hook(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            bool committed;
            await _diagnosticPublicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_publicationGate)
                {
                    committed = sequence == Volatile.Read(ref _reloadSequence) &&
                        capturedGeneration == _workspaceSetGeneration &&
                        ReferenceEquals(published, _workspaces) &&
                        ReferenceEquals(previous, _primaryWorkspace);
                    if (committed)
                    {
                        _primaryWorkspace = candidate;
                        if (rediscovery is not null)
                            _configurationPath = rediscovery.ConfigurationPath;
                        Volatile.Write(ref _workspaces, unpublished.ToArray());
                        _workspaceSetGeneration++;
                    }
                }
            }
            finally { _diagnosticPublicationGate.Release(); }
            if (!committed) return false;

            unpublished.Clear();
            RetireWorkspaces(published, "retired Workspace after reload");
            try { ScheduleAllOpenDiagnostics(); }
            catch (Exception exception)
            {
                TryLogLifecycleFailure("post-reload diagnostic scheduling", exception);
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _log.WriteLineAsync($"LSP workspace reload rejected; keeping last valid state: {exception.Message}");
            return false;
        }
        finally
        {
            RetireWorkspaces(unpublished, "unpublished reload candidate");
        }
    }

    private static Dictionary<string, OpenDocumentState> CaptureOpenDocumentStates(
        IEnumerable<Xenon.ProjectSystem.Workspace> workspaces)
    {
        var states = new Dictionary<string, OpenDocumentState>(DocumentUri.PathComparer);
        foreach (DocumentSnapshot document in workspaces.SelectMany(workspace =>
                     workspace.CurrentSnapshot.Documents).Where(document =>
                     document.IsOpen && document.PhysicalPath is not null))
        {
            string path = DocumentUri.NormalizePath(document.PhysicalPath!);
            var state = new OpenDocumentState(document.OverlayText!.Text, document.Version);
            if (states.TryGetValue(path, out OpenDocumentState? existing) && existing != state)
                throw new InvalidOperationException(
                    $"Open editor state for '{path}' is inconsistent across Workspace contexts.");
            states[path] = state;
        }
        return states;
    }

    private sealed record OpenDocumentState(string OverlayText, DocumentVersion Version);

    private async Task ReloadExternalSourceAsync(string uri,
        CancellationToken cancellationToken)
    {
        await _diagnosticPublicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
            ScheduleAffectedOpenDiagnostics(contexts.Select(item => item.Workspace));
        }
        finally { _diagnosticPublicationGate.Release(); }
    }

    private void ScheduleAllOpenDiagnostics()
    {
        Workspace[] workspaces;
        long generation;
        lock (_publicationGate)
        {
            workspaces = _workspaces;
            generation = _workspaceSetGeneration;
        }
        foreach (Xenon.ProjectSystem.Workspace workspace in workspaces)
        {
            string[] affected = _openUris.Values.Where(uri =>
                !_resolver.ResolveAll(workspace.CurrentSnapshot, uri).IsEmpty).ToArray();
            _diagnostics.ScheduleMany(workspace, affected, generation);
        }
    }

    private void AppendWorkspace(Xenon.ProjectSystem.Workspace workspace)
    {
        lock (_publicationGate)
        {
            Workspace[] current = _workspaces;
            Volatile.Write(ref _workspaces, [.. current, workspace]);
            _workspaceSetGeneration++;
        }
    }

    private void PruneClosedAuxiliaryWorkspaces()
    {
        Workspace[] retired;
        lock (_publicationGate)
        {
            Workspace[] current = _workspaces;
            Workspace[] retained = current.Where(workspace =>
                ReferenceEquals(workspace, _primaryWorkspace) ||
                workspace.CurrentSnapshot.Documents.Any(document => document.IsOpen)).ToArray();
            retired = current.Except(retained).ToArray();
            if (retired.Length != 0)
            {
                Volatile.Write(ref _workspaces, retained);
                _workspaceSetGeneration++;
            }
        }
        RetireWorkspaces(retired, "closed auxiliary Workspace");
    }

    private void TrackAndSchedule(string uri, IEnumerable<Xenon.ProjectSystem.Workspace> changed)
    {
        _openUris[DocumentUri.ToNormalizedPath(uri)] = uri;
        Volatile.Write(ref _knownUriCount, _openUris.Count);
        ScheduleAffectedOpenDiagnostics(changed);
    }

    private void ScheduleAffectedOpenDiagnostics(
        IEnumerable<Xenon.ProjectSystem.Workspace> changed)
    {
        Workspace[] published;
        long generation;
        lock (_publicationGate)
        {
            published = _workspaces;
            generation = _workspaceSetGeneration;
        }
        foreach (Xenon.ProjectSystem.Workspace workspace in changed.Distinct()
                     .Where(published.Contains))
        {
            IEnumerable<string> affected = _openUris.Values.Where(openUri =>
                !_resolver.ResolveAll(workspace.CurrentSnapshot, openUri).IsEmpty);
            _diagnostics.ScheduleMany(workspace, affected, generation);
        }
    }

    private List<(Xenon.ProjectSystem.Workspace Workspace, DocumentContext Context)> ResolveContexts(string uri)
    {
        var result = new List<(Xenon.ProjectSystem.Workspace, DocumentContext)>();
        foreach (Xenon.ProjectSystem.Workspace workspace in Volatile.Read(ref _workspaces))
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
        try { await _diagnostics.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            TryLogLifecycleFailure("diagnostic scheduler disposal", exception);
        }
        Workspace[] retired;
        lock (_publicationGate)
        {
            retired = _workspaces;
            Volatile.Write(ref _workspaces, []);
            _primaryWorkspace = null;
            _workspaceDiscoveryRoot = null;
            _workspaceSetGeneration++;
        }
        RetireWorkspaces(retired, "session Workspace disposal");
        _diagnosticPublicationGate.Dispose();
        _mutationGate.Dispose();
    }

    private void RetireWorkspaces(IEnumerable<Xenon.ProjectSystem.Workspace> workspaces,
        string operation)
    {
        foreach (Xenon.ProjectSystem.Workspace workspace in workspaces.Distinct())
        {
            try { workspace.Dispose(); }
            catch (Exception exception)
            {
                TryLogLifecycleFailure(operation, exception);
            }
        }
    }

    private void TryLogLifecycleFailure(string operation, Exception exception)
    {
        try { _log.WriteLine($"LSP {operation} failed: {exception}"); }
        catch
        {
            // Cleanup must continue even if the configured lifecycle log is unavailable.
        }
    }
}
