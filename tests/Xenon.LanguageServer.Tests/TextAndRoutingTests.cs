using System.Collections.Immutable;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class TextAndRoutingTests
{
    [Theory]
    [InlineData("", 0, 0, 0)]
    [InlineData("abc\n", 0, 3, 3)]
    [InlineData("abc\r\ndef", 1, 2, 7)]
    [InlineData("a😀b", 0, 3, 3)]
    [InlineData("\n", 1, 0, 1)]
    public void Utf16PositionsRoundTrip(string text, int line, int character, int offset)
    {
        SourceText source = SourceText.From(text);
        Assert.Equal(offset, LspTextCoordinates.ToOffset(source, new LspPosition(line, character)));
        Assert.Equal(new LspPosition(line, character), LspTextCoordinates.ToPosition(source, offset));
    }

    [Fact]
    public void Utf16RejectsLineBreakColumnsAndInvalidRanges()
    {
        SourceText source = SourceText.From("a\r\nb");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LspTextCoordinates.ToOffset(source, new LspPosition(0, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LspTextCoordinates.ToTextSpan(source,
                new LspRange(new LspPosition(1, 0), new LspPosition(0, 0))));
    }

    [Fact]
    public void ResolverRepresentsSharedFileAndPrefersRootProject()
    {
        using var directory = new TestDirectory();
        string shared = directory.Write("shared/common.xe", "fn common() {}\n");
        string rootProjectPath = directory.Write("Root.xeproj", "");
        string otherProjectPath = directory.Write("Other.xeproj", "");
        var root = Project("Root", rootProjectPath, shared);
        var other = Project("Other", otherProjectPath, shared);
        using var workspace = Workspace.Create(XenonProjectGraph.Create(root, [root, other]));
        var resolver = new DocumentContextResolver();
        string uri = DocumentUri.FromPath(shared).AbsoluteUri;

        ImmutableArray<DocumentContext> contexts = resolver.ResolveAll(workspace.CurrentSnapshot, uri);

        Assert.Equal(2, contexts.Length);
        Assert.True(contexts[0].IsRootProject);
        Assert.Equal(workspace.CurrentSnapshot.RootProjectId, resolver.ResolvePrimary(
            workspace.CurrentSnapshot, uri).ProjectId);
    }

    [Fact]
    public void UriRoundTripsSpacesAndUnicode()
    {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine("unicode space", "Ж main.xe"));
        Uri uri = DocumentUri.FromPath(path);
        Assert.Contains("%20", uri.AbsoluteUri);
        Assert.True(DocumentUri.PathComparer.Equals(path, DocumentUri.ToNormalizedPath(uri.AbsoluteUri)));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void LspVersionMappingPreservesTheFullSignedDomain(int version)
    {
        DocumentVersion mapped = LspDocumentVersions.FromLsp(version);
        Assert.True(mapped > DocumentVersion.Initial);
        Assert.Equal(version, LspDocumentVersions.ToLsp(mapped));
    }

    private static XenonProject Project(string name, string projectPath, string source) => new(
        name, XenonProjectType.Executable, null, System.IO.Path.GetDirectoryName(projectPath)!,
        System.IO.Path.GetDirectoryName(source)!, projectPath, [source], [], [], [],
        XenonBuildProfile.Debug, XenonBuildProfile.Release);
}
