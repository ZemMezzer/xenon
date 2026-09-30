namespace Xenon.Compiler;

/// <summary>Internal ABI names shared by the compiler, LLVM backend and native runtime.
/// These are never Xenon source identifiers. See docs/compiler/runtime-abi.md.</summary>
internal static class RuntimeAbiNames
{
    public const string Prefix = "__xenon_";
    public static bool IsReservedIdentifier(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    public const string AlignedMalloc = Prefix + "aligned_malloc";
    public const string AsyncRootClose = Prefix + "async_root_close";
    public const string AsyncRootCreate = Prefix + "async_root_create";
    public const string AsyncRootNotify = Prefix + "async_root_notify";
    public const string AsyncRootPump = Prefix + "async_root_pump";
    public const string AsyncRootRelease = Prefix + "async_root_release";
    public const string AsyncRootRetain = Prefix + "async_root_retain";
    public const string Calloc = Prefix + "calloc";
    public const string EhAbandon = Prefix + "eh_abandon";
    public const string EhActivate = Prefix + "eh_activate";
    public const string EhAllocate = Prefix + "eh_allocate";
    public const string EhCleanup = Prefix + "eh_cleanup";
    public const string EhCurrent = Prefix + "eh_current";
    public const string EhHandle = Prefix + "eh_handle";
    public const string EhInitialize = Prefix + "eh_initialize";
    public const string EhMatches = Prefix + "eh_matches";
    public const string EhObject = Prefix + "eh_object";
    public const string EhReplacePrevious = Prefix + "eh_replace_previous";
    public const string EhRethrow = Prefix + "eh_rethrow";
    public const string EhTerminate = Prefix + "eh_terminate";
    public const string EhThrow = Prefix + "eh_throw";
    public const string Free = Prefix + "free";
    public const string Malloc = Prefix + "malloc";
    public const string ResumeContinuation = Prefix + "resume_continuation";
    public const string ResumeCreate = Prefix + "resume_create";
    public const string ResumeDrain = Prefix + "resume_drain";
    public const string ResumeRelease = Prefix + "resume_release";
    public const string ResumeRequest = Prefix + "resume_request";
    public const string ResumeStart = Prefix + "resume_start";
    public const string ResumeTokenDrop = Prefix + "resume_token_drop";

    public static readonly string[] ResumableHelpers =
    [
        ResumeContinuation,
        ResumeCreate,
        ResumeDrain,
        ResumeRelease,
        ResumeRequest,
        ResumeStart,
        ResumeTokenDrop,
    ];
}
