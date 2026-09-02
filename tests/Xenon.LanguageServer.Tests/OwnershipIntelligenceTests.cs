using System.Text.Json;
using System.Threading.Channels;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class OwnershipIntelligenceTests
{
    [Fact]
    public async Task OwnershipTypesMembersAndOperationsHaveCompilerBackedEditorIdentity()
    {
        const string source = """
            namespace App;
            struct Resource
            {
                public int Count;
                public Resource(int count) { Count = count; }
                public void Use() {}
                public int readonly GetCount() { return Count; }
                public ~Resource() {}
            }
            struct Holder
            {
                public unique<Resource> Owned;
                public shared<Resource> Shared;
                public weak<Resource> Weak;
                public storage<Resource> Slot;
                public pin<Resource> Pinned;
            }
            void Test(unique<Resource> owned, shared<Resource> shared,
                Resource& mutableRef, readonly Resource& readonlyRef)
            {
                storage<Resource> slot = Resource(0);
                slot.Count = 10;
                slot.Use();
                owned->Use();
                shared->Use();
                mutableRef.Count = 1;
                int observed = readonlyRef.GetCount();
                unique<Resource> moved = move owned;
                destruct(slot);
                Resource* raw = new Resource(1);
                free(raw);
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, source);

        JsonElement tokensResponse = Result(await session.HandleRequestAsync(
            "textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        IReadOnlyList<(string Text, int Type, int Modifiers)> tokens =
            DecodeTokens(source, tokensResponse.GetProperty("data"));

        Assert.True(tokens.Count(token => token.Text == "Resource" && token.Type == 1) >= 10);
        foreach (string keyword in new[] { "unique", "shared", "weak", "storage", "pin", "move", "destruct", "free" })
            Assert.True(tokens.Any(token => token.Text == keyword && token.Type == 15),
                $"Missing semantic keyword token '{keyword}'.");
        foreach (string field in new[] { "Owned", "Shared", "Weak", "Slot", "Pinned" })
            Assert.Contains(tokens, token => token.Text == field && token.Type == 9);
        Assert.Contains(tokens, token => token.Text == "Count" && token.Type == 9);
        Assert.Contains(tokens, token => token.Text == "Use" && token.Type == 6);

        JsonElement slotHover = await RequestAtAsync(session, "textDocument/hover", uri, source,
            source.IndexOf("slot =", StringComparison.Ordinal));
        Assert.Contains("storage<Resource> slot",
            slotHover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement mutableHover = await RequestAtAsync(session, "textDocument/hover", uri, source,
            source.IndexOf("mutableRef,", StringComparison.Ordinal));
        Assert.Contains("Resource& mutableRef",
            mutableHover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement readonlyHover = await RequestAtAsync(session, "textDocument/hover", uri, source,
            source.IndexOf("readonlyRef)", StringComparison.Ordinal));
        Assert.Contains("readonly Resource& readonlyRef",
            readonlyHover.GetProperty("contents").GetProperty("value").GetString());

        int containedType = source.IndexOf("storage<Resource> slot", StringComparison.Ordinal) + "storage<".Length;
        JsonElement typeDefinition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            containedType);
        Assert.Equal(uri, Assert.Single(typeDefinition.EnumerateArray()).GetProperty("uri").GetString());
        int projectedField = source.IndexOf("slot.Count", StringComparison.Ordinal) + "slot.".Length;
        JsonElement fieldDefinition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            projectedField);
        Assert.Equal(3, Assert.Single(fieldDefinition.EnumerateArray()).GetProperty("range")
            .GetProperty("start").GetProperty("line").GetInt32());
        JsonElement fieldHover = await RequestAtAsync(session, "textDocument/hover", uri, source,
            projectedField);
        Assert.Contains("public int Count",
            fieldHover.GetProperty("contents").GetProperty("value").GetString());
        int ownedMethod = source.IndexOf("owned->Use", StringComparison.Ordinal) + "owned->".Length;
        JsonElement methodDefinition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            ownedMethod);
        Assert.Equal(5, Assert.Single(methodDefinition.EnumerateArray()).GetProperty("range")
            .GetProperty("start").GetProperty("line").GetInt32());

        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", uri, source,
            projectedField, newName: "Total");
        Assert.True(rename.GetProperty("changes").GetProperty(uri).GetArrayLength() >= 4);
    }

    [Fact]
    public async Task OwnershipCompletionAndSignatureHelpUseProjectedCompilerTypes()
    {
        const string source = """
            namespace App;
            struct Resource
            {
                public int Count;
                public Resource(int count) { Count = count; }
                public void Use() {}
                public int readonly GetCount() { return Count; }
            }
            struct Container<T> { public storage<T> Value; }
            void StorageCompletion()
            {
                storage<Resource> slot = Resource(0);
                int count = slot.Count;
            }
            void UniqueCompletion(unique<Resource> owned) { owned->Use(); }
            void SharedCompletion(shared<Resource> owned) { owned->Use(); }
            void ReferenceCompletion(Resource& value) { value.Use(); }
            void ReadonlyReferenceCompletion(readonly Resource& value) { int count = value.GetCount(); }
            void SpecializedCompletion(Container<Resource> container)
            {
                container.Value = Resource(0);
                int count = container.Value.Count;
            }
            void ConstructorHelp()
            {
                storage<Resource> slot = Resource(0);
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, source);

        foreach ((string marker, string prefix) in new[]
                 {
                     ("slot.Count", "slot."),
                     ("owned->Use", "owned->"),
                     ("value.Use", "value."),
                     ("container.Value.Count", "container.Value."),
                 })
        {
            int position = source.IndexOf(marker, StringComparison.Ordinal) + prefix.Length;
            JsonElement completion = await RequestAtAsync(session, "textDocument/completion", uri, source, position);
            string[] labels = completion.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("label").GetString()!).ToArray();
            Assert.Contains("Count", labels);
            Assert.Contains("Use", labels);
        }

        int sharedPosition = source.IndexOf("owned->Use", source.IndexOf("SharedCompletion", StringComparison.Ordinal),
            StringComparison.Ordinal) + "owned->".Length;
        JsonElement sharedCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            sharedPosition);
        Assert.Contains(sharedCompletion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "Use");

        int signaturePosition = source.LastIndexOf("Resource(", StringComparison.Ordinal) + "Resource(".Length;
        JsonElement signature = await RequestAtAsync(session, "textDocument/signatureHelp", uri, source,
            signaturePosition);
        Assert.Contains("Resource(int count)",
            signature.GetProperty("signatures")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task OwnershipHoverAndNestedTypeCompletionPreserveExactTypes()
    {
        const string hoverSource = """
            namespace App;
            struct Resource {}
            void Inspect(unique<Resource> uniqueValue, shared<Resource> sharedValue,
                weak<Resource> weakValue, Resource& mutableRef, readonly Resource& readonlyRef,
                storage<Resource>& storageRef, readonly storage<Resource>& readonlyStorageRef)
            {
                pin<Resource> pinned = Resource();
                storage<unique<Resource>> nested;
                pin<storage<Resource>> pinnedStorage;
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("hover.xe", hoverSource);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using (LanguageServerSession session = await CreateSessionAsync(uri, hoverSource))
        {
            foreach ((string marker, string expected) in new[]
                     {
                         ("uniqueValue,", "unique<Resource> uniqueValue"),
                         ("sharedValue,", "shared<Resource> sharedValue"),
                         ("weakValue,", "weak<Resource> weakValue"),
                         ("mutableRef,", "Resource& mutableRef"),
                         ("readonlyRef,", "readonly Resource& readonlyRef"),
                         ("storageRef,", "storage<Resource>& storageRef"),
                         ("readonlyStorageRef)", "readonly storage<Resource>& readonlyStorageRef"),
                         ("pinned =", "pin<Resource> pinned"),
                         ("nested;", "storage<unique<Resource>> nested"),
                         ("pinnedStorage;", "pin<storage<Resource>> pinnedStorage"),
                     })
            {
                JsonElement hover = await RequestAtAsync(session, "textDocument/hover", uri, hoverSource,
                    hoverSource.IndexOf(marker, StringComparison.Ordinal));
                Assert.Contains(expected,
                    hover.GetProperty("contents").GetProperty("value").GetString());
            }
        }

        foreach (string partial in new[]
                 {
                     "storage<Res", "unique<Res", "shared<Res", "weak<Res", "pin<Res",
                     "storage<unique<Res",
                 })
        {
            string completionSource = $"namespace App; struct Resource {{}} void Test() {{ {partial}";
            using var completionDirectory = new TestDirectory();
            string completionFile = completionDirectory.Write("completion.xe", completionSource);
            string completionUri = DocumentUri.FromPath(completionFile).AbsoluteUri;
            await using LanguageServerSession completionSession =
                await CreateSessionAsync(completionUri, completionSource);
            JsonElement completion = await RequestAtAsync(completionSession, "textDocument/completion",
                completionUri, completionSource, completionSource.Length);
            Assert.Contains(completion.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("label").GetString() == "Resource");
        }
    }

    [Fact]
    public async Task CompilerOwnershipDiagnosticsArePublishedByTheLanguageServer()
    {
        const string source = """
            namespace App;
            struct Resource { public int Value; public void Use() {} public ~Resource() {} }
            struct Child { public int Value; public ~Child() {} }
            struct Parent
            {
                public Child Child;
                public Child TakeChild() { return move Child; }
                public void DestroyChild() { destruct(Child); }
            }
            Resource& Forward(Resource& value) { return value; }
            Resource& Select(bool condition, Resource& first, Resource& second)
            {
                if (condition) return first;
                return second;
            }
            interface ISource { Resource& Get(); }
            struct View
            {
                public Resource& Resource;
                public void Kill() { destruct(Resource); }
                public Resource& Get() { return Resource; }
            }
            void RawPointer(Resource* pointer)
            {
                destruct(*pointer);
                destruct(pointer[0]);
                Resource value = move *pointer;
            }
            void MovedValue()
            {
                Resource first = Resource();
                Resource second = move first;
                first.Use();
            }
            void EmptyStorage()
            {
                storage<Resource> slot;
                slot.Use();
            }
            void ActiveBorrow()
            {
                Resource value = Resource();
                Resource& reference = value;
                destruct(value);
                reference.Use();
            }
            void InvalidLock()
            {
                Resource value = Resource();
                lock value;
            }
            void DiscardedOwnership(unique<Resource> owned, weak<Resource> observer)
            {
                move owned;
                lock observer;
                new Resource();
            }
            void PartialStorageLifetime()
            {
                storage<Resource> slot = Resource();
                destruct(slot.Value);
                int value = move slot.Value;
            }
            void PartialStorageLifetimeThroughMethods()
            {
                storage<Parent> slot = Parent();
                Child child = slot.TakeChild();
                slot.DestroyChild();
            }
            void ReferenceParameterLifetime(Resource& resource)
            {
                destruct(resource);
                Resource moved = move resource;
            }
            void ReferenceParameterChildLifetime(Parent& parent)
            {
                destruct(parent.Child);
                Child moved = move parent.Child;
            }
            void ReferenceParameterIndirectLifetime(Parent& parent)
            {
                parent.DestroyChild();
            }
            void ForwardedReferenceParameterLifetime(Resource& resource)
            {
                destruct(Forward(resource));
            }
            void ForwardedStorageValueLifetime()
            {
                storage<Resource> value = Resource();
                Resource moved = move Forward(value);
            }
            void ReturnedReferenceFieldLifetime(Resource& resource)
            {
                View view = View { resource };
                destruct(view.Get());
            }
            void UnknownReferenceLifetime(ISource& source)
            {
                destruct(source.Get());
            }
            void MultipleReferenceLifetime(bool condition)
            {
                Resource first = Resource();
                Resource second = Resource();
                destruct(Select(condition, first, second));
            }
            void SharedCondition(shared<Resource> owner)
            {
                if (owner) {}
            }
            void BorrowedMove()
            {
                Resource value = Resource();
                Resource& reference = value;
                Resource moved = move value;
                reference.Use();
            }
            void ReconstructStorage()
            {
                storage<Resource> slot = Resource();
                slot = Resource();
            }
            void MutateThroughStorageValueReference()
            {
                storage<Resource> slot = Resource();
                Resource& reference = slot;
                destruct(reference);
            }
            void MutateReadonlyStorage(readonly storage<Resource>& reference)
            {
                destruct(reference);
            }
            Resource& EscapeReference()
            {
                Resource local = Resource();
                return local;
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("diagnostics.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var notifications = Channel.CreateUnbounded<JsonElement>();
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics")
                notifications.Writer.TryWrite(Result(value));
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, uri, source);
        JsonElement published = await ReadDiagnosticsAsync(notifications.Reader, version: 1);
        string[] codes = published.GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("code").GetString()!).ToArray();

        Assert.Contains(DiagnosticIds.HeapPointeeExplicitDestruction, codes);
        Assert.Contains(DiagnosticIds.InvalidMoveSource, codes);
        Assert.Contains(DiagnosticIds.UseAfterMove, codes);
        Assert.Contains(DiagnosticIds.StorageNotInitialized, codes);
        Assert.Contains(DiagnosticIds.DestructWhileBorrowed, codes);
        Assert.Contains(DiagnosticIds.MoveWhileBorrowed, codes);
        Assert.Contains(DiagnosticIds.InvalidLockOperand, codes);
        Assert.Equal(3, codes.Count(code => code == DiagnosticIds.UnconsumedOwnershipExpression));
        Assert.Equal(4, codes.Count(code => code == DiagnosticIds.PartialStorageLifetimeOperation));
        Assert.Equal(6, codes.Count(code => code == DiagnosticIds.ReferenceParameterLifetimeMutation));
        Assert.Equal(4, codes.Count(code => code == DiagnosticIds.UnresolvedLifetimeOwner));
        Assert.Contains(DiagnosticIds.InvalidCondition, codes);
        Assert.Contains(DiagnosticIds.StorageAlreadyInitialized, codes);
        Assert.Contains(DiagnosticIds.StorageValueLifetimeMutation, codes);
        Assert.Contains(DiagnosticIds.InvalidAssignmentTarget, codes);
        Assert.Contains(DiagnosticIds.EscapingLocalReference, codes);
    }

    [Fact]
    public async Task StorageProjectionAndDestructorBoundaryDiagnosticsUseCompilerSemantics()
    {
        const string source = """
            namespace App;
            struct Child { public ~Child() {} }
            struct Parent
            {
                public Child Child;
                public ~Parent() {}
            }
            Child& GetStorageChild(storage<Parent>& value) { return value.Child; }
            Child& GetChild(Parent& value) { return value.Child; }
            void Test()
            {
                storage<Parent> slot = Parent();
                destruct(GetStorageChild(slot));
                Parent parent = Parent();
                destruct(parent.Child);
                destruct(GetChild(parent));
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("lifetime-boundaries.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var notifications = Channel.CreateUnbounded<JsonElement>();
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics")
                notifications.Writer.TryWrite(Result(value));
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, uri, source);

        JsonElement published = await ReadDiagnosticsAsync(notifications.Reader, version: 1);
        string[] codes = published.GetProperty("diagnostics").EnumerateArray()
            .Select(diagnostic => diagnostic.GetProperty("code").GetString()!).ToArray();

        Assert.True(codes.Count(code => code == DiagnosticIds.StorageValueLifetimeMutation) == 1,
            $"Published diagnostics: {string.Join(", ", codes)}");
        Assert.Equal(2, codes.Count(code => code == DiagnosticIds.PartialDestructWithDestructor));
        Assert.Equal(3, codes.Length);
    }

    [Fact]
    public async Task OwnershipDiagnosticsRefreshAcrossNonLexicalLifetimeEdits()
    {
        const string valid = """
            namespace App;
            struct Resource
            {
                public int Count;
                public int readonly GetCount() { return Count; }
                public ~Resource() {}
            }
            void Test()
            {
                storage<Resource> slot = Resource();
                Resource& resource = slot;
                resource.Count = 10;
                int value = resource.GetCount();
                destruct(slot);
            }
            """;
        const string invalid = """
            namespace App;
            struct Resource
            {
                public int Count;
                public int readonly GetCount() { return Count; }
                public ~Resource() {}
            }
            void Test()
            {
                storage<Resource> slot = Resource();
                Resource& resource = slot;
                destruct(slot);
                int value = resource.GetCount();
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", valid);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var notifications = Channel.CreateUnbounded<JsonElement>();
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics")
                notifications.Writer.TryWrite(Result(value));
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, uri, valid);

        JsonElement initial = await ReadDiagnosticsAsync(notifications.Reader, version: 1);
        Assert.Empty(initial.GetProperty("diagnostics").EnumerateArray());

        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = invalid } },
        }), default);
        JsonElement changed = await ReadDiagnosticsAsync(notifications.Reader, version: 2);
        Assert.Contains(changed.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == DiagnosticIds.DestructWhileBorrowed);

        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 3 },
            contentChanges = new[] { new { text = valid } },
        }), default);
        JsonElement fixedDiagnostics = await ReadDiagnosticsAsync(notifications.Reader, version: 3);
        Assert.Empty(fixedDiagnostics.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task StorageFlowDiagnosticsRefreshForEmptyInitializedAndDestroyedStates()
    {
        const string empty = """
            namespace App;
            struct Resource { public void Use() {} public ~Resource() {} }
            void Test() { storage<Resource> slot; slot.Use(); }
            """;
        const string initialized = """
            namespace App;
            struct Resource { public void Use() {} public ~Resource() {} }
            void Test() { storage<Resource> slot; slot = Resource(); slot.Use(); }
            """;
        const string destroyed = """
            namespace App;
            struct Resource { public void Use() {} public ~Resource() {} }
            void Test() { storage<Resource> slot; slot = Resource(); destruct(slot); slot.Use(); }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("storage-flow.xe", empty);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var notifications = Channel.CreateUnbounded<JsonElement>();
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics")
                notifications.Writer.TryWrite(Result(value));
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, uri, empty);

        JsonElement emptyDiagnostics = await ReadDiagnosticsAsync(notifications.Reader, version: 1);
        Assert.Contains(emptyDiagnostics.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == DiagnosticIds.StorageNotInitialized);

        await ChangeAsync(session, uri, initialized, version: 2);
        JsonElement initializedDiagnostics = await ReadDiagnosticsAsync(notifications.Reader, version: 2);
        Assert.Empty(initializedDiagnostics.GetProperty("diagnostics").EnumerateArray());

        await ChangeAsync(session, uri, destroyed, version: 3);
        JsonElement destroyedDiagnostics = await ReadDiagnosticsAsync(notifications.Reader, version: 3);
        Assert.Contains(destroyedDiagnostics.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
            diagnostic.GetProperty("code").GetString() == DiagnosticIds.StorageNotInitialized);
    }

    [Fact]
    public async Task LockExpressionAndArrowCompletionUseCompilerSemantics()
    {
        const string source = """
            namespace App;
            struct Resource { public int Count; public void Use() {} }
            void Test(Resource direct, Resource* raw, unique<Resource> uniqueValue,
                shared<Resource> sharedValue, weak<Resource> weakValue)
            {
                direct.Use();
                raw->Use();
                uniqueValue->Use();
                sharedValue->Use();
                shared<Resource> locked = lock weakValue;
                locked->Use();
                weakValue.Lock();
                weakValue->Use();
                bool comparison = 1 > 0;
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("weak-lock.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        JsonElement initialize = Result(await session.HandleRequestAsync("initialize",
            LspTestProtocol.Json(new { rootUri = uri }), default));
        string[] triggers = initialize.GetProperty("capabilities").GetProperty("completionProvider")
            .GetProperty("triggerCharacters").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains(".", triggers);
        Assert.Contains(">", triggers);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = source },
        }), default);

        foreach (string marker in new[] { "raw->Use", "uniqueValue->Use", "sharedValue->Use" })
        {
            int position = source.IndexOf(marker, StringComparison.Ordinal) + marker.IndexOf("->", StringComparison.Ordinal) + 2;
            JsonElement completion = await RequestAtAsync(session, "textDocument/completion", uri, source,
                position, requestContext: new { triggerKind = 2, triggerCharacter = ">" });
            Assert.Contains(completion.GetProperty("items").EnumerateArray(), item =>
                item.GetProperty("label").GetString() == "Use");
        }

        int directPosition = source.IndexOf("direct.Use", StringComparison.Ordinal) + "direct.".Length;
        JsonElement directCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            directPosition, requestContext: new { triggerKind = 2, triggerCharacter = "." });
        Assert.Contains(directCompletion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "Use");

        int weakDot = source.IndexOf("weakValue.Lock", StringComparison.Ordinal) + "weakValue.".Length;
        JsonElement weakCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            weakDot, requestContext: new { triggerKind = 2, triggerCharacter = "." });
        Assert.Empty(weakCompletion.GetProperty("items").EnumerateArray());

        int weakArrow = source.IndexOf("weakValue->Use", StringComparison.Ordinal) + "weakValue->".Length;
        JsonElement weakArrowCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            weakArrow, requestContext: new { triggerKind = 2, triggerCharacter = ">" });
        Assert.Empty(weakArrowCompletion.GetProperty("items").EnumerateArray());
        int standaloneGreater = source.IndexOf("> 0", StringComparison.Ordinal) + 1;
        JsonElement standaloneCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            standaloneGreater, requestContext: new { triggerKind = 2, triggerCharacter = ">" });
        Assert.Empty(standaloneCompletion.GetProperty("items").EnumerateArray());

        int lockPosition = source.IndexOf("lock weakValue", StringComparison.Ordinal);
        JsonElement hover = await RequestAtAsync(session, "textDocument/hover", uri, source, lockPosition);
        Assert.Contains("shared<Resource>",
            hover.GetProperty("contents").GetProperty("value").GetString());

        int expressionPosition = source.IndexOf("{\n", source.IndexOf("void Test", StringComparison.Ordinal),
            StringComparison.Ordinal) + 2;
        JsonElement expressionCompletion = await RequestAtAsync(session, "textDocument/completion", uri, source,
            expressionPosition);
        Assert.Contains(expressionCompletion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "lock" && item.GetProperty("kind").GetInt32() == 14);

        int lockedPosition = source.IndexOf("locked =", StringComparison.Ordinal);
        JsonElement lockedHover = await RequestAtAsync(session, "textDocument/hover", uri, source, lockedPosition);
        Assert.Contains("shared<Resource> locked",
            lockedHover.GetProperty("contents").GetProperty("value").GetString());
        int weakPosition = source.IndexOf("weakValue)", StringComparison.Ordinal);
        JsonElement weakHover = await RequestAtAsync(session, "textDocument/hover", uri, source, weakPosition);
        Assert.Contains("weak<Resource> weakValue",
            weakHover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement tokensResponse = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        IReadOnlyList<(string Text, int Type, int Modifiers)> tokens =
            DecodeTokens(source, tokensResponse.GetProperty("data"));
        Assert.Contains(tokens, token => token.Text == "lock" && token.Type == 15);
        Assert.Contains(tokens, token => token.Text == "Use" && token.Type == 6);
        Assert.DoesNotContain(tokens, token => token.Text == "Lock" && token.Type == 6);
    }

    private static async Task<LanguageServerSession> CreateSessionAsync(string uri, string source)
    {
        var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, uri, source);
        return session;
    }

    private static async Task InitializeAsync(LanguageServerSession session, string uri, string source)
    {
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = source },
        }), default);
    }

    private static Task ChangeAsync(LanguageServerSession session, string uri, string source, int version) =>
        session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text = source } },
        }), default);

    private static async Task<JsonElement> RequestAtAsync(LanguageServerSession session, string method,
        string uri, string source, int offset, string? newName = null, object? requestContext = null)
    {
        LspPosition position = LspTextCoordinates.ToPosition(SourceText.From(source), offset);
        object parameters = method == "textDocument/rename"
            ? new { textDocument = new { uri }, position, newName }
            : requestContext is not null
                ? new { textDocument = new { uri }, position, context = requestContext }
            : new { textDocument = new { uri }, position };
        return Result(await session.HandleRequestAsync(method, LspTestProtocol.Json(parameters), default));
    }

    private static async Task<JsonElement> ReadDiagnosticsAsync(ChannelReader<JsonElement> reader, int version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await reader.WaitToReadAsync(timeout.Token))
            while (reader.TryRead(out JsonElement notification))
                if (notification.GetProperty("version").GetInt32() == version)
                    return notification;
        throw new TimeoutException($"Diagnostics for version {version} were not published.");
    }

    private static IReadOnlyList<(string Text, int Type, int Modifiers)> DecodeTokens(
        string source, JsonElement data)
    {
        int line = 0;
        int character = 0;
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int[] values = data.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var result = new List<(string, int, int)>();
        for (int index = 0; index < values.Length; index += 5)
        {
            line += values[index];
            character = values[index] == 0 ? character + values[index + 1] : values[index + 1];
            result.Add((lines[line].Substring(character, values[index + 2]),
                values[index + 3], values[index + 4]));
        }
        return result;
    }

    private static JsonElement Result(object? value) => JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
