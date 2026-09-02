using System.Collections.Immutable;
using System.Text.Json;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

/// <summary>Protocol translation for compiler/Workspace-owned intelligence.</summary>
internal static class LspCoreIntelligence
{
    internal static readonly string[] SemanticTokenTypes =
    [
        "namespace", "type", "interface", "enum", "enumMember", "function", "method",
        "constructor", "property", "field", "parameter", "variable", "constant",
        "typeParameter", "modifier", "keyword",
    ];
    internal static readonly string[] SemanticTokenModifiers = ["declaration", "definition", "static", "readonly"];

    public static async Task<object?> HandleDocumentRequestAsync(LanguageServerAnalysisContext context,
        string method, JsonElement parameters)
    {
        LspPosition position = method is "textDocument/documentSymbol" or "textDocument/semanticTokens/full"
            ? default : ReadPosition(RequireObject(parameters, "position"));
        return method switch
        {
            "textDocument/hover" => await HoverAsync(context, position),
            "textDocument/definition" => await DefinitionAsync(context, position, typeDefinition: false),
            "textDocument/typeDefinition" => await DefinitionAsync(context, position, typeDefinition: true),
            "textDocument/references" => await ReferencesAsync(context, position,
                parameters.TryGetProperty("context", out JsonElement referenceContext) &&
                referenceContext.TryGetProperty("includeDeclaration", out JsonElement include) && include.GetBoolean()),
            "textDocument/implementation" => await ImplementationsAsync(context, position),
            "textDocument/documentSymbol" => await DocumentSymbolsAsync(context),
            "textDocument/completion" => await CompletionAsync(context, position, parameters),
            "textDocument/signatureHelp" => await SignatureHelpAsync(context, position),
            "textDocument/semanticTokens/full" => await SemanticTokensAsync(context),
            "textDocument/prepareRename" => await PrepareRenameAsync(context, position),
            "textDocument/rename" => await RenameAsync(context, position, RequireString(parameters, "newName")),
            _ => throw new JsonRpcException(LspErrorCodes.MethodNotFound, $"Method '{method}' is not supported."),
        };
    }

    public static async Task<object?> WorkspaceSymbolsAsync(WorkspaceSnapshot snapshot, string query,
        CancellationToken cancellationToken)
    {
        WorkspaceSymbolIndex index = await snapshot.GetSymbolIndexAsync(cancellationToken).ConfigureAwait(false);
        return index.Entries.Select(entry => (Entry: entry, Rank: MatchRank(entry, query)))
            .Where(item => item.Rank >= 0)
            .OrderBy(item => item.Rank).ThenBy(item => item.Entry.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Entry.Id.ProjectId)
            .Select(item => new
            {
                name = item.Entry.Name,
                kind = SymbolKindNumber(item.Entry.EditorKind),
                location = ToLocation(snapshot, item.Entry.Declaration),
                containerName = ContainerName(item.Entry.QualifiedName),
            }).ToArray();
    }

    public static async Task<object?> DiagnosticsAsync(LanguageServerAnalysisContext context)
    {
        SemanticModel model = await context.Project.GetSemanticModelAsync(context.Document.Id,
            context.CancellationToken).ConfigureAwait(false);
        return model.GetDiagnostics(context.CancellationToken)
            .Where(diagnostic => diagnostic.Location.Source.FileId == context.Document.SourceFileId)
            .Select(diagnostic => new
            {
                range = ToRange(diagnostic.Location.Source, diagnostic.Location.Span),
                severity = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
                code = diagnostic.Id,
                source = "xenon",
                message = diagnostic.Message,
                relatedInformation = diagnostic.RelatedLocations.IsEmpty ? null :
                    diagnostic.RelatedLocations.Select(related => new
                    {
                        location = new LspLocation(DocumentUri.FromPath(related.Location.Path).AbsoluteUri,
                            ToRange(related.Location.Source, related.Location.Span)),
                        message = related.Message ?? diagnostic.Message,
                    }).ToArray(),
            }).ToArray();
    }

    private static async Task<object?> HoverAsync(LanguageServerAnalysisContext context, LspPosition lspPosition)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        if (symbol is null)
        {
            LockExpressionSyntax? @lock = SyntaxNavigator.DescendantNodesAndSelf(context.Document.SyntaxTree.Root)
                .OfType<LockExpressionSyntax>()
                .Where(candidate => position >= candidate.LockKeyword.Location.Span.Start &&
                    position <= candidate.LockKeyword.Location.Span.End)
                .OrderBy(candidate => SyntaxNavigator.GetSpan(candidate).Length).FirstOrDefault();
            if (@lock is null) return null;
            TypeSymbol type = model.GetTypeInfo(@lock, context.CancellationToken).Type;
            if (type is ErrorTypeSymbol) return null;
            return new
            {
                contents = new { kind = "markdown", value = $"```xenon\n{type.ToDisplayString()}\n```" },
                range = ToRange(context.Document.EffectiveText, @lock.LockKeyword.Location.Span),
            };
        }
        symbol = UnwrapAlias(symbol);
        string display = HoverDisplay(symbol);
        return new
        {
            contents = new { kind = "markdown", value = $"```xenon\n{display}\n```" },
            range = SymbolRangeAtPosition(model, context.Document.SyntaxTree, position),
        };
    }

    private static async Task<object?> DefinitionAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition, bool typeDefinition)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        if (symbol is null) return null;
        symbol = UnwrapAlias(symbol);
        if (typeDefinition) symbol = SemanticModel.GetAssociatedDeclaredType(symbol);
        if (symbol is null || !symbol.IsSourceDefined) return null;
        SourceReference[] workspaceDeclarations = context.Snapshot.TryGetSymbolId(symbol,
            out WorkspaceSymbolId id)
            ? (await context.Snapshot.GetSymbolIndexAsync(context.CancellationToken)
                .ConfigureAwait(false)).Entries.Where(entry => entry.Id == id)
                .Select(entry => entry.Declaration).ToArray()
            : [];
        IEnumerable<LspLocation> locations = workspaceDeclarations.Length == 0
            ? symbol.DeclaringSyntaxReferences.Select(reference => ToLocation(reference.Location))
            : workspaceDeclarations.Select(reference => ToLocation(context.Snapshot, reference));
        return locations.DistinctBy(location => (location.Uri, location.Range)).ToArray();
    }

    private static async Task<object?> ReferencesAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition, bool includeDeclaration)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        if (symbol is null || !context.Snapshot.TryGetSymbolId(UnwrapAlias(symbol), out WorkspaceSymbolId id))
            return Array.Empty<LspLocation>();
        WorkspaceReferenceIndex index = await context.Snapshot.GetReferenceIndexAsync(context.CancellationToken)
            .ConfigureAwait(false);
        IEnumerable<LspLocation> locations = index.FindReferences(id)
            .Select(entry => ToLocation(context.Snapshot, entry.Source));
        if (includeDeclaration)
            locations = locations.Concat(symbol.DeclaringSyntaxReferences.Select(reference => ToLocation(reference.Location)));
        return locations.DistinctBy(location => (location.Uri, location.Range))
            .OrderBy(location => location.Uri, StringComparer.Ordinal)
            .ThenBy(location => location.Range.Start.Line).ThenBy(location => location.Range.Start.Character).ToArray();
    }

    private static async Task<object?> ImplementationsAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        if (symbol is null) return Array.Empty<LspLocation>();
        symbol = GetImplementationTarget(UnwrapAlias(symbol));
        if (!context.Snapshot.TryGetSymbolId(symbol, out WorkspaceSymbolId id))
            return Array.Empty<LspLocation>();

        if (symbol is DeclaredTypeSymbol)
        {
            WorkspaceTypeRelationshipIndex typeIndex = await context.Snapshot
                .GetTypeRelationshipIndexAsync(context.CancellationToken).ConfigureAwait(false);
            return typeIndex.FindTransitive(id)
                .Select(entry => ToLocation(context.Snapshot, entry.DerivedDeclaration))
                .DistinctBy(location => (location.Uri, location.Range)).ToArray();
        }

        WorkspaceMemberRelationshipIndex memberIndex = await context.Snapshot
            .GetMemberRelationshipIndexAsync(context.CancellationToken).ConfigureAwait(false);
        WorkspaceSymbolIndex symbolIndex = await context.Snapshot
            .GetSymbolIndexAsync(context.CancellationToken).ConfigureAwait(false);
        HashSet<WorkspaceSymbolId> implementations = memberIndex.FindImplementations(id).ToHashSet();
        return symbolIndex.Entries.Where(entry => implementations.Contains(entry.Id))
            .Select(entry => ToLocation(context.Snapshot, entry.Declaration))
            .DistinctBy(location => (location.Uri, location.Range)).ToArray();
    }

    private static async Task<object?> DocumentSymbolsAsync(LanguageServerAnalysisContext context)
    {
        SemanticModel model = await context.Project.GetSemanticModelAsync(context.Document.Id,
            context.CancellationToken).ConfigureAwait(false);
        Symbol[] symbols = model.GetDeclaredSymbols(context.CancellationToken)
            .Where(symbol => EditorSymbolClassifier.IsEditorVisible(symbol) &&
                symbol is not LocalVariableSymbol and not ParameterSymbol &&
                symbol.DeclaringSyntaxReferences.Any(reference =>
                    reference.Source.FileId == context.Document.SourceFileId))
            .ToArray();
        var nodes = new Dictionary<Symbol, LspDocumentSymbol>(ReferenceEqualityComparer.Instance);
        foreach (Symbol symbol in symbols)
        {
            SyntaxReference declaration = symbol.DeclaringSyntaxReferences.First(reference =>
                reference.Source.FileId == context.Document.SourceFileId);
            nodes[symbol] = new LspDocumentSymbol
            {
                Name = symbol.Name,
                Detail = symbol.ToDisplayString(SymbolDisplayFormat.Signature),
                Kind = SymbolKindNumber(EditorSymbolClassifier.GetKind(symbol)),
                Range = ToRange(declaration.Source, symbol is NamespaceSymbol
                    ? TextSpan.FromBounds(SyntaxNavigator.GetSpan(declaration.Declaration).Start,
                        declaration.Source.Length)
                    : SyntaxNavigator.GetSpan(declaration.Declaration)),
                SelectionRange = ToRange(declaration.Source, declaration.Span),
            };
        }
        var roots = new List<LspDocumentSymbol>();
        foreach (Symbol symbol in symbols)
        {
            LspDocumentSymbol node = nodes[symbol];
            if (symbol.ContainingSymbol is { } parent && nodes.TryGetValue(parent, out LspDocumentSymbol? parentNode))
            {
                parentNode.Children ??= [];
                parentNode.Children.Add(node);
            }
            else roots.Add(node);
        }
        Sort(roots);
        return roots;

        static void Sort(List<LspDocumentSymbol> items)
        {
            items.Sort((left, right) => Compare(left.SelectionRange.Start, right.SelectionRange.Start));
            foreach (LspDocumentSymbol item in items)
                if (item.Children is not null) Sort(item.Children);
        }
    }

    private static async Task<object?> CompletionAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition, JsonElement parameters)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        if (IsTriggerCharacter(parameters, ">") &&
            (position < 2 || context.Document.EffectiveText[position - 2] != '-' ||
             context.Document.EffectiveText[position - 1] != '>'))
            return new { isIncomplete = false, items = Array.Empty<object>() };
        MemberAccessExpressionSyntax? access = SyntaxNavigator.DescendantNodesAndSelf(context.Document.SyntaxTree.Root)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(candidate => candidate.OperatorToken.Location.Span.End <= position &&
                position <= Math.Max(candidate.MemberToken.Location.Span.End, candidate.OperatorToken.Location.Span.End + 1))
            .OrderByDescending(candidate => candidate.OperatorToken.Location.Span.Start).FirstOrDefault();
        IEnumerable<Symbol> symbols = access is null
            ? model.GetCompletionSymbols(context.Document.SyntaxTree, position, context.CancellationToken)
            : model.GetCompletionSymbols(access, position, context.CancellationToken);
        var items = symbols.Where(symbol => symbol.Kind != SymbolKind.Error)
            .DistinctBy(symbol => (symbol.Name, EditorSymbolClassifier.GetKind(symbol),
                symbol.ToDisplayString(SymbolDisplayFormat.Signature)))
            .Select(symbol => CompletionItem(symbol)).ToList();
        if (access is null)
            items.AddRange(SyntaxFacts.GetKeywordTexts().Select(keyword => new
            {
                label = keyword, kind = 14, detail = "keyword", insertText = keyword,
                sortText = "9_" + keyword, filterText = keyword,
            }));
        return new { isIncomplete = false, items = items.ToArray() };
    }

    private static async Task<object?> SignatureHelpAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        SyntaxNode? call = SyntaxNavigator.DescendantNodesAndSelf(context.Document.SyntaxTree.Root)
            .Where(node => node is CallExpressionSyntax or NewExpressionSyntax or IndexExpressionSyntax)
            .Where(node => CallStart(node) <= position && position <= CallEnd(node))
            .OrderBy(node => SyntaxNavigator.GetSpan(node).Length).FirstOrDefault();
        if (call is null) return null;
        SymbolInfo info = model.GetSymbolInfo(call, context.CancellationToken);
        var seenCandidates = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        Symbol[] candidates = new[] { info.Symbol }.OfType<Symbol>().Concat(info.CandidateSymbols)
            .Where(symbol => symbol is FunctionSymbol or IndexerSymbol or InterfaceIndexerSymbol or
                SyntheticMemberSymbol { MemberKind: SyntheticMemberKind.Method })
            .Where(seenCandidates.Add).ToArray();
        if (candidates.Length == 0) return null;
        int activeParameter = Commas(call).Count(token => token.Location.Span.Start < position);
        var signatures = candidates.Select(candidate => new
        {
            label = candidate.ToDisplayString(SymbolDisplayFormat.Signature),
            parameters = Parameters(candidate).Select(parameter => new
            {
                label = parameter.ToDisplayString(SymbolDisplayFormat.Signature),
            }).ToArray(),
        }).ToArray();
        int maxParameter = signatures.Max(signature => signature.parameters.Length);
        return new { signatures, activeSignature = 0,
            activeParameter = maxParameter == 0 ? 0 : Math.Min(activeParameter, maxParameter - 1) };
    }

    private static async Task<object?> SemanticTokensAsync(LanguageServerAnalysisContext context)
    {
        SemanticModel model = await context.Project.GetSemanticModelAsync(context.Document.Id,
            context.CancellationToken).ConfigureAwait(false);
        var tokens = new List<(TextSpan Span, int Type, int Modifiers, int Priority)>();
        foreach (Symbol symbol in model.GetDeclaredSymbols(context.CancellationToken)
                     .Where(EditorSymbolClassifier.IsEditorVisible))
            foreach (TextLocation location in symbol.Locations.Where(location =>
                         location.Source.FileId == context.Document.SourceFileId))
            {
                int type = SemanticTokenType(symbol, declaration: true);
                if (type >= 0)
                    tokens.Add((location.Span, type, SemanticModifiers(symbol, declaration: true), 2));
            }
        foreach (ResolvedSymbolReference reference in model.GetResolvedReferences(context.CancellationToken))
            if (EditorSymbolClassifier.IsEditorVisible(reference.Symbol) &&
                reference.Location.Source.FileId == context.Document.SourceFileId)
            {
                int type = SemanticTokenType(reference.Symbol, declaration: false);
                if (type >= 0)
                    tokens.Add((reference.Location.Span, type,
                        SemanticModifiers(reference.Symbol, declaration: false), 1));
            }
        AddOwnershipLanguageTokens(context.Document.SyntaxTree, tokens);
        var ordered = tokens.Where(item => item.Span.Length > 0)
            .GroupBy(item => item.Span).Select(group => group.OrderByDescending(item => item.Priority).First())
            .OrderBy(item => item.Span.Start).ToArray();
        var data = new List<int>(ordered.Length * 5);
        int previousLine = 0, previousCharacter = 0;
        foreach (var item in ordered)
        {
            LspRange range = ToRange(context.Document.EffectiveText, item.Span);
            if (range.Start.Line != range.End.Line) continue;
            int deltaLine = range.Start.Line - previousLine;
            int deltaCharacter = deltaLine == 0 ? range.Start.Character - previousCharacter : range.Start.Character;
            data.Add(deltaLine); data.Add(deltaCharacter); data.Add(range.End.Character - range.Start.Character);
            data.Add(item.Type);
            data.Add(item.Modifiers);
            previousLine = range.Start.Line; previousCharacter = range.Start.Character;
        }
        return new { data = data.ToArray() };
    }

    private static void AddOwnershipLanguageTokens(SyntaxTree tree,
        List<(TextSpan Span, int Type, int Modifiers, int Priority)> tokens)
    {
        const int keywordType = 15;
        foreach (SyntaxToken token in tree.Tokens.Where(token => token.Kind is
                     SyntaxKind.UniqueKeyword or SyntaxKind.SharedKeyword or SyntaxKind.WeakKeyword or
                     SyntaxKind.StorageKeyword or SyntaxKind.PinKeyword))
            tokens.Add((token.Location.Span, keywordType, 0, 3));

        foreach (MoveExpressionSyntax move in SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
                     .OfType<MoveExpressionSyntax>())
            tokens.Add((move.MoveKeyword.Location.Span, keywordType, 0, 3));
        foreach (LockExpressionSyntax @lock in SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
                     .OfType<LockExpressionSyntax>())
            tokens.Add((@lock.LockKeyword.Location.Span, keywordType, 0, 3));
        foreach (FreeExpressionSyntax free in SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
                     .OfType<FreeExpressionSyntax>())
            tokens.Add((free.FreeKeyword.Location.Span, keywordType, 0, 3));
        foreach (CallExpressionSyntax call in SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
                     .OfType<CallExpressionSyntax>())
            if (call.TypeArguments is null && call.Target is NameExpressionSyntax name &&
                name.IdentifierToken.Text == "destruct")
                tokens.Add((name.IdentifierToken.Location.Span, keywordType, 0, 3));
    }

    private static async Task<object?> PrepareRenameAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition)
    {
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        symbol = GetRenameTarget(symbol);
        EnsureRenameable(symbol);
        return new { range = SymbolRangeAtPosition(model, context.Document.SyntaxTree, position),
            placeholder = symbol!.Name };
    }

    private static async Task<object?> RenameAsync(LanguageServerAnalysisContext context,
        LspPosition lspPosition, string newName)
    {
        if (!IsIdentifier(newName)) throw InvalidParams($"'{newName}' is not a valid Xenon identifier.");
        (SemanticModel model, int position) = await ModelAndPositionAsync(context, lspPosition);
        Symbol? symbol = FindSymbol(model, context.Document.SyntaxTree, position);
        symbol = GetRenameTarget(symbol);
        EnsureRenameable(symbol);
        Symbol target = symbol!;
        if (string.Equals(target.Name, newName, StringComparison.Ordinal)) return new { changes = new { } };
        if (!context.Snapshot.TryGetSymbolId(target, out WorkspaceSymbolId id))
            throw InvalidParams("The symbol has no editable Workspace identity.");
        WorkspaceSymbolIndex symbols = await context.Snapshot.GetSymbolIndexAsync(context.CancellationToken)
            .ConfigureAwait(false);
        WorkspaceMemberRelationshipIndex relationships = await context.Snapshot
            .GetMemberRelationshipIndexAsync(context.CancellationToken).ConfigureAwait(false);
        var family = new HashSet<WorkspaceSymbolId>();
        var pending = new Queue<WorkspaceSymbolId>();
        bool targetIsIndexed = symbols.Entries.Any(entry => entry.Id == id);
        if (!targetIsIndexed) family.Add(id);
        pending.Enqueue(id);
        while (pending.TryDequeue(out WorkspaceSymbolId current))
        {
            if (!symbols.Entries.Any(entry => entry.Id == current)) continue;
            SharedPhysicalDeclarationGroup shared = symbols.GetSharedPhysicalDeclarationGroup(current);
            if (!shared.IsCompatible)
                throw InvalidParams("The physical declaration has incompatible semantic identities across projects.");
            foreach (WorkspaceSymbolId related in shared.Entries.Select(entry => entry.Id)
                         .Concat(relationships.GetFamily(current)))
                if (family.Add(related)) pending.Enqueue(related);
        }
        if (relationships.HasNonEditableMember(family) || family.Select(member =>
                symbols.Entries.SingleOrDefault(entry => entry.Id == member))
                .OfType<SymbolIndexEntry>().Any(entry => !entry.CanRename))
            throw InvalidParams("The complete semantic member family is not safely editable.");
        if (model.HasRenameConflict(target, newName, context.CancellationToken) ||
            await context.Snapshot.HasRenameConflictAsync(family, newName, context.CancellationToken)
                .ConfigureAwait(false))
            throw InvalidParams($"Renaming to '{newName}' would create a duplicate declaration.");
        WorkspaceReferenceIndex index = await context.Snapshot.GetReferenceIndexAsync(context.CancellationToken)
            .ConfigureAwait(false);
        ReferenceIndexEntry[] familyReferences = index.Entries.Where(entry => family.Contains(entry.Target)).ToArray();
        var familyPhysicalReferences = familyReferences.Select(entry => PhysicalKey(entry.Source)).ToHashSet();
        if (index.Entries.Any(entry => familyPhysicalReferences.Contains(PhysicalKey(entry.Source)) &&
                !family.Contains(entry.Target) && !IsOwnedConstructor(entry.Target)))
            throw InvalidParams("A shared physical reference has incompatible semantic targets across projects.");

        var occurrences = familyReferences.Select(entry => entry.Source).ToList();
        foreach (WorkspaceSymbolId member in family)
        {
            SymbolIndexEntry? declaration = symbols.Entries.SingleOrDefault(entry => entry.Id == member);
            if (declaration is null)
            {
                if (member != id || context.Snapshot.GetDeclaration(target) is not { } directDeclaration)
                    throw InvalidParams("The complete semantic member family is not safely editable.");
                if (context.Snapshot.Documents.Count(document => document.PhysicalPath is not null &&
                        string.Equals(DocumentUri.FromPath(document.PhysicalPath).AbsoluteUri,
                            DocumentUri.FromPath(directDeclaration.Path).AbsoluteUri,
                            StringComparison.Ordinal)) > 1)
                    throw InvalidParams("A shared physical declaration cannot be proven semantically compatible.");
                occurrences.Add(directDeclaration);
                continue;
            }
            occurrences.Add(declaration.Declaration);
            if (declaration.EditorKind == EditorSymbolKind.Struct)
                occurrences.AddRange(symbols.FindMembers(member).Where(entry =>
                        entry.FunctionKind is FunctionKind.Constructor or FunctionKind.Destructor)
                    .Select(entry => entry.Declaration));
        }

        SourceReference[] normalized = occurrences
            .GroupBy(PhysicalKey)
            .Select(group => group.First()).ToArray();
        foreach (IGrouping<string, SourceReference> document in normalized.GroupBy(item =>
                     DocumentUri.FromPath(item.Path).AbsoluteUri, StringComparer.Ordinal))
        {
            SourceReference[] ordered = document.OrderBy(item => item.Span.Start)
                .ThenBy(item => item.Span.Length).ToArray();
            for (int positionIndex = 1; positionIndex < ordered.Length; positionIndex++)
                if (ordered[positionIndex - 1].Span.End > ordered[positionIndex].Span.Start)
                    throw InvalidParams("Rename would create conflicting overlapping physical edits.");
        }
        var changes = normalized
            .GroupBy(item => DocumentUri.FromPath(item.Path).AbsoluteUri, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(item => item.Span.Start)
                    .Select(item => new LspTextEdit(ToRange(context.Snapshot.GetDocument(item.DocumentId).EffectiveText,
                        item.Span), newName)).ToArray(), StringComparer.Ordinal);
        return new { changes };

        static (string Uri, TextSpan Span) PhysicalKey(SourceReference source) =>
            (DocumentUri.FromPath(source.Path).AbsoluteUri, source.Span);

        bool IsOwnedConstructor(WorkspaceSymbolId candidate) => symbols.Entries
            .SingleOrDefault(entry => entry.Id == candidate) is
            { FunctionKind: FunctionKind.Constructor, ContainingSymbolId: { } owner } && family.Contains(owner);
    }

    private static void EnsureRenameable(Symbol? symbol)
    {
        if (symbol is null) throw InvalidParams("No resolved symbol exists at the requested position.");
        if (symbol is AliasSymbol)
            throw InvalidParams("Using-alias rename is not supported because references bind to the target symbol.");
        if (symbol is NamespaceSymbol)
            throw InvalidParams("Namespace rename is not supported as one namespace may have multiple declarations.");
        if (!EditorSymbolClassifier.CanRename(symbol))
            throw InvalidParams("The symbol has no safe user-editable identifier.");
    }

    private static Symbol? GetRenameTarget(Symbol? symbol) => symbol is FunctionSymbol
        { FunctionKind: FunctionKind.Constructor or FunctionKind.Destructor, ContainingType: not null } function
        ? function.ContainingType : symbol;

    private static async Task<(SemanticModel Model, int Position)> ModelAndPositionAsync(
        LanguageServerAnalysisContext context, LspPosition lspPosition)
    {
        int position;
        try { position = LspTextCoordinates.ToOffset(context.Document.EffectiveText, lspPosition); }
        catch (ArgumentOutOfRangeException exception) { throw InvalidParams(exception.Message); }
        SemanticModel model = await context.Project.GetSemanticModelAsync(context.Document.Id,
            context.CancellationToken).ConfigureAwait(false);
        return (model, position);
    }

    private static Symbol? FindSymbol(SemanticModel model, SyntaxTree tree, int position)
    {
        SymbolInfo info = model.GetSymbolInfoAtPosition(tree, position);
        if (info.Symbol is null && position > 0) info = model.GetSymbolInfoAtPosition(tree, position - 1);
        return info.Symbol;
    }

    private static Symbol UnwrapAlias(Symbol symbol) => symbol is AliasSymbol alias ? alias.Target : symbol;

    private static Symbol GetImplementationTarget(Symbol symbol) => symbol switch
    {
        FunctionSymbol { ContainingProperty: not null } function => function.ContainingProperty,
        FunctionSymbol { ContainingInterfaceProperty: not null } function =>
            function.ContainingInterfaceProperty,
        _ => symbol,
    };

    private static string HoverDisplay(Symbol symbol)
    {
        string display = symbol.ToDisplayString(SymbolDisplayFormat.Declaration);
        if (symbol is StructTypeSymbol structure)
        {
            var bases = new[] { structure.BaseType?.ToDisplayString(TypeDisplayFormat.FullyQualified) }
                .OfType<string>().Concat(structure.Interfaces.Select(item =>
                    item.ToDisplayString(TypeDisplayFormat.FullyQualified))).ToArray();
            if (bases.Length > 0) display += " : " + string.Join(", ", bases);
        }
        else if (symbol is InterfaceTypeSymbol @interface && !@interface.BaseInterfaces.IsEmpty)
            display += " : " + string.Join(", ", @interface.BaseInterfaces.Select(item =>
                item.ToDisplayString(TypeDisplayFormat.FullyQualified)));
        else if (symbol is ConstantSymbol { HasValue: true } constant)
            display += $" = {constant.Value ?? "null"}";
        else if (symbol is FieldSymbol { ConstantValue: not null } field)
            display += $" = {field.ConstantValue}";
        return display;
    }

    private static LspRange? SymbolRangeAtPosition(SemanticModel model, SyntaxTree tree, int position)
    {
        SyntaxToken? token = tree.Tokens.Where(candidate => position >= candidate.Location.Span.Start &&
            position <= candidate.Location.Span.End).OrderBy(candidate => candidate.Location.Span.Length).FirstOrDefault();
        return token is null ? null : ToRange(tree.Source, token.Location.Span);
    }

    private static object CompletionItem(Symbol symbol) => new
    {
        label = symbol.Name,
        kind = CompletionKind(EditorSymbolClassifier.GetKind(symbol)),
        detail = symbol.ToDisplayString(SymbolDisplayFormat.Signature),
        insertText = symbol.Name,
        sortText = "0_" + symbol.Name,
        filterText = symbol.Name,
    };

    private static int CompletionKind(EditorSymbolKind kind) => kind switch
    {
        EditorSymbolKind.Method => 2,
        EditorSymbolKind.Function => 3,
        EditorSymbolKind.Constructor => 4,
        EditorSymbolKind.Field => 5,
        EditorSymbolKind.LocalVariable or EditorSymbolKind.Parameter => 6,
        EditorSymbolKind.Type => 7,
        EditorSymbolKind.Interface or EditorSymbolKind.Template => 8,
        EditorSymbolKind.Namespace => 9,
        EditorSymbolKind.Property => 10,
        EditorSymbolKind.Enum => 13,
        EditorSymbolKind.EnumMember => 20,
        EditorSymbolKind.Constant => 21,
        EditorSymbolKind.Struct => 22,
        EditorSymbolKind.TypeParameter => 25,
        _ => 1,
    };

    private static int SymbolKindNumber(EditorSymbolKind kind) => kind switch
    {
        EditorSymbolKind.Namespace => 3,
        EditorSymbolKind.Struct => 23,
        EditorSymbolKind.Interface or EditorSymbolKind.Template => 11,
        EditorSymbolKind.Enum => 10,
        EditorSymbolKind.EnumMember => 22,
        EditorSymbolKind.Function => 12,
        EditorSymbolKind.Method => 6,
        EditorSymbolKind.Constructor => 9,
        EditorSymbolKind.Property => 7,
        EditorSymbolKind.Field => 8,
        EditorSymbolKind.Constant => 14,
        EditorSymbolKind.Parameter or EditorSymbolKind.LocalVariable => 13,
        EditorSymbolKind.TypeParameter => 26,
        _ => 5,
    };

    private static int SemanticTokenType(EditorSymbolKind kind) => kind switch
    {
        EditorSymbolKind.Namespace => 0,
        EditorSymbolKind.Type or EditorSymbolKind.Struct => 1,
        EditorSymbolKind.Interface or EditorSymbolKind.Template => 2,
        EditorSymbolKind.Enum => 3,
        EditorSymbolKind.EnumMember => 4,
        EditorSymbolKind.Function => 5,
        EditorSymbolKind.Method => 6,
        // Constructors are spelled with their containing type's name and should
        // therefore receive the same editor color as that type.
        EditorSymbolKind.Constructor => 1,
        EditorSymbolKind.Property => 8,
        EditorSymbolKind.Field => 9,
        EditorSymbolKind.Parameter => 10,
        EditorSymbolKind.LocalVariable => 11,
        EditorSymbolKind.Constant => 12,
        EditorSymbolKind.TypeParameter => 13,
        _ => -1,
    };

    private static int SemanticTokenType(Symbol symbol, bool declaration)
    {
        if (declaration && symbol is IndexerSymbol or InterfaceIndexerSymbol)
            return 14;
        if (IsImplicitSetterValue(symbol))
            return 14;
        return SemanticTokenType(EditorSymbolClassifier.GetKind(symbol));
    }

    private static bool IsImplicitSetterValue(Symbol symbol)
    {
        if (symbol is not ParameterSymbol
            {
                Name: "value",
                IsSourceDefined: false,
                ContainingSymbol: FunctionSymbol accessor,
            })
            return false;
        return ReferenceEquals(accessor.ContainingProperty?.Setter, accessor) ||
               ReferenceEquals(accessor.ContainingIndexer?.Setter, accessor);
    }

    private static int SemanticModifiers(Symbol symbol, bool declaration)
    {
        int value = declaration ? 1 : 0;
        if (declaration && symbol.IsDefinition) value |= 2;
        if (symbol is FieldSymbol { IsStatic: true } or FunctionSymbol { IsStatic: true }) value |= 4;
        if (symbol is VariableSymbol { IsReadonly: true } or FieldSymbol { IsReadonly: true } or
            FunctionSymbol { IsReadonly: true }) value |= 8;
        return value;
    }

    private static IEnumerable<ParameterSymbol> Parameters(Symbol symbol) => symbol switch
    {
        FunctionSymbol function => function.Parameters,
        IndexerSymbol indexer => indexer.Parameters,
        InterfaceIndexerSymbol indexer => indexer.Parameters,
        SyntheticMemberSymbol member => member.Parameters,
        _ => [],
    };

    private static bool IsTriggerCharacter(JsonElement parameters, string character) =>
        parameters.TryGetProperty("context", out JsonElement context) &&
        context.TryGetProperty("triggerKind", out JsonElement kind) && kind.GetInt32() == 2 &&
        context.TryGetProperty("triggerCharacter", out JsonElement trigger) &&
        trigger.GetString() == character;

    private static IEnumerable<SyntaxToken> Commas(SyntaxNode node) => node switch
    {
        CallExpressionSyntax call => call.CommaTokens,
        NewExpressionSyntax creation => creation.CommaTokens,
        IndexExpressionSyntax index => index.CommaTokens,
        _ => [],
    };

    private static int CallStart(SyntaxNode node) => node switch
    {
        CallExpressionSyntax call => call.OpenParenthesisToken.Location.Span.End,
        NewExpressionSyntax creation => creation.OpenDelimiterToken.Location.Span.End,
        IndexExpressionSyntax index => index.OpenBracketToken.Location.Span.End,
        _ => int.MaxValue,
    };

    private static int CallEnd(SyntaxNode node) => node switch
    {
        CallExpressionSyntax call => call.CloseParenthesisToken.IsMissing ? call.CloseParenthesisToken.Location.Span.Start : call.CloseParenthesisToken.Location.Span.End,
        NewExpressionSyntax creation => creation.CloseDelimiterToken.IsMissing ? creation.CloseDelimiterToken.Location.Span.Start : creation.CloseDelimiterToken.Location.Span.End,
        IndexExpressionSyntax index => index.CloseBracketToken.IsMissing ? index.CloseBracketToken.Location.Span.Start : index.CloseBracketToken.Location.Span.End,
        _ => -1,
    };

    private static int MatchRank(SymbolIndexEntry entry, string query)
    {
        if (string.IsNullOrEmpty(query)) return 2;
        if (entry.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            entry.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return -1;
    }

    private static string? ContainerName(string qualifiedName)
    {
        int separator = qualifiedName.LastIndexOf('.');
        return separator < 0 ? null : qualifiedName[..separator];
    }

    private static LspLocation ToLocation(TextLocation location) =>
        new(DocumentUri.FromPath(location.Path).AbsoluteUri, ToRange(location.Source, location.Span));

    private static LspLocation ToLocation(WorkspaceSnapshot snapshot, SourceReference source) =>
        new(DocumentUri.FromPath(source.Path).AbsoluteUri,
            ToRange(snapshot.GetDocument(source.DocumentId).EffectiveText, source.Span));

    private static LspRange ToRange(SourceText source, TextSpan span) =>
        LspTextCoordinates.ToRange(source, span);

    private static int Compare(LspPosition left, LspPosition right) =>
        left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Character.CompareTo(right.Character);

    private static bool IsIdentifier(string text) => !string.IsNullOrEmpty(text) &&
        SyntaxFacts.GetKeywordKind(text) == SyntaxKind.IdentifierToken &&
        (char.IsLetter(text[0]) || text[0] == '_') && text.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static JsonElement RequireObject(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.Object
            ? property : throw InvalidParams($"'{name}' must be an object.");

    private static string RequireString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw InvalidParams($"'{name}' must be a string.");

    private static LspPosition ReadPosition(JsonElement value) => new(
        value.GetProperty("line").GetInt32(), value.GetProperty("character").GetInt32());

    private static JsonRpcException InvalidParams(string message) => new(LspErrorCodes.InvalidParams, message);
}
