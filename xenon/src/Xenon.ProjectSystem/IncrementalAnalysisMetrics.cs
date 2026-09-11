namespace Xenon.ProjectSystem;

/// <summary>Deterministic counters for one Workspace publication; no process-global state.</summary>
public sealed record IncrementalAnalysisMetrics(
    int DocumentsChanged = 0,
    int DocumentsReparsed = 0,
    int SyntaxTreesReused = 0,
    int ProjectsInvalidated = 0,
    int ProjectsReused = 0,
    int CompilationsRebuilt = 0,
    int CompilationsReused = 0,
    int SemanticModelsReused = 0,
    int SymbolIndexesRebuilt = 0,
    int SymbolIndexDocumentsReused = 0,
    int ReferenceIndexesRebuilt = 0,
    int ReferenceIndexDocumentsReused = 0)
{
    public static IncrementalAnalysisMetrics Initial(int documentCount, int projectCount) => new(
        DocumentsChanged: documentCount,
        DocumentsReparsed: documentCount,
        ProjectsInvalidated: projectCount,
        CompilationsRebuilt: projectCount,
        SymbolIndexesRebuilt: projectCount,
        ReferenceIndexesRebuilt: projectCount);
}
