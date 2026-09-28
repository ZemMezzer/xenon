using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.CodeGen.LLVM;

/// <summary>Scheduler-independent continuation ownership and serialized resume requests.</summary>
internal static unsafe class LlvmResumableRuntime
{
    public static void LinkAndLower(LLVMModuleRef module, int pointerBits)
    {
        string ir = RuntimeIr.Replace("WORD", $"i{pointerBits}").Replace("ALIGN", (pointerBits / 8).ToString());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(ir);
        LLVMModuleRef runtime;
        fixed (byte* text = bytes)
        {
            sbyte* name = stackalloc sbyte[1];
            name[0] = 0;
            LLVMMemoryBufferRef buffer = new((nint)LLVMApi.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)text, (nuint)bytes.Length, name));
            runtime = module.Context.ParseIR(buffer);
        }
        runtime.Target = module.Target;
        runtime.DataLayout = module.DataLayout;
        var roots = new List<(LLVMValueRef Function, LLVMLinkage Linkage)>();
        for (LLVMValueRef function = module.FirstFunction; function != default; function = function.NextFunction)
            if (function.Name.EndsWith(".resumable", StringComparison.Ordinal) ||
                function.Name is "__xenon_malloc" or "__xenon_free")
            {
                roots.Add((function, function.Linkage));
                function.Linkage = LLVMLinkage.LLVMExternalLinkage;
            }
        if (LLVMApi.LinkModules2(module, runtime) != 0)
            throw new LlvmCodeGenerationException("Could not link resumable continuation helpers.");
        using LLVMPassBuilderOptionsRef options = LLVMPassBuilderOptionsRef.Create();
        nint pipeline = Marshal.StringToCoTaskMemUTF8("coro-early,function(mem2reg),cgscc(coro-split),coro-cleanup");
        try
        {
            LLVMErrorRef error = LLVMApi.RunPasses(module, (sbyte*)pipeline, default, options);
            if (error == default) return;
            sbyte* message = LLVMApi.GetErrorMessage(error);
            try { throw new LlvmCodeGenerationException(Marshal.PtrToStringUTF8((nint)message) ?? "Coroutine lowering failed."); }
            finally { LLVMApi.DisposeErrorMessage(message); }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pipeline);
            foreach ((LLVMValueRef function, LLVMLinkage linkage) in roots) function.Linkage = linkage;
        }
    }

    // Each registration has its own once flag and epoch. Stale or duplicate callbacks
    // retain only the small control record, never a pointer to freed coroutine storage.
    // Gate bits: running=1, requested=2, terminal=4. Only the running owner touches the frame.
    private const string RuntimeIr = """
        %xe.resume.state = type { WORD, ptr, i32, WORD }
        %xe.resume.token = type { ptr, WORD, i32 }
        %xe.function.control = type { WORD, ptr, ptr }
        declare ptr @__xenon_malloc(WORD)
        declare void @__xenon_free(ptr)
        declare i1 @llvm.coro.done(ptr)
        declare void @llvm.coro.resume(ptr)
        declare void @llvm.coro.destroy(ptr)

        define linkonce_odr ptr @__xenon_resume_create() {
        entry:
          %state = call ptr @__xenon_malloc(WORD ptrtoint (ptr getelementptr (%xe.resume.state, ptr null, i32 1) to WORD))
          store WORD 1, ptr %state
          %handle = getelementptr %xe.resume.state, ptr %state, i32 0, i32 1
          store ptr null, ptr %handle
          %gate = getelementptr %xe.resume.state, ptr %state, i32 0, i32 2
          store i32 1, ptr %gate
          %epoch = getelementptr %xe.resume.state, ptr %state, i32 0, i32 3
          store WORD 0, ptr %epoch
          ret ptr %state
        }

        define linkonce_odr void @__xenon_resume_release(ptr %state) {
        entry:
          %old = atomicrmw sub ptr %state, WORD 1 release
          %last = icmp eq WORD %old, 1
          br i1 %last, label %destroy, label %end
        destroy:
          fence acquire
          call void @__xenon_free(ptr %state)
          br label %end
        end:
          ret void
        }

        define linkonce_odr void @__xenon_resume_token_drop(ptr %token) {
        entry:
          %state = load ptr, ptr %token
          call void @__xenon_resume_release(ptr %state)
          call void @__xenon_free(ptr %token)
          ret void
        }

        define linkonce_odr void @__xenon_resume_drain(ptr %state) {
        entry:
          %address = getelementptr %xe.resume.state, ptr %state, i32 0, i32 1
          %handle = load ptr, ptr %address
          %gate = getelementptr %xe.resume.state, ptr %state, i32 0, i32 2
          br label %check
        check:
          %done = call i1 @llvm.coro.done(ptr %handle)
          br i1 %done, label %finish, label %release
        release:
          %claim = cmpxchg ptr %gate, i32 1, i32 0 release monotonic
          %released = extractvalue { i32, i1 } %claim, 1
          br i1 %released, label %end, label %resume
        resume:
          %pending = atomicrmw xchg ptr %gate, i32 1 acq_rel
          call void @llvm.coro.resume(ptr %handle)
          br label %check
        finish:
          store atomic i32 4, ptr %gate release, align 4
          call void @llvm.coro.destroy(ptr %handle)
          call void @__xenon_resume_release(ptr %state)
          br label %end
        end:
          ret void
        }

        define linkonce_odr void @__xenon_resume_start(ptr %state, ptr %handle) {
        entry:
          %address = getelementptr %xe.resume.state, ptr %state, i32 0, i32 1
          store ptr %handle, ptr %address
          call void @__xenon_resume_drain(ptr %state)
          ret void
        }

        define linkonce_odr void @__xenon_resume_request(ptr %token) {
        entry:
          %used = getelementptr %xe.resume.token, ptr %token, i32 0, i32 2
          %old = atomicrmw xchg ptr %used, i32 1 acq_rel
          %first = icmp eq i32 %old, 0
          br i1 %first, label %validate, label %end
        validate:
          %state = load ptr, ptr %token
          %expectedAddress = getelementptr %xe.resume.token, ptr %token, i32 0, i32 1
          %expected = load WORD, ptr %expectedAddress
          %epochAddress = getelementptr %xe.resume.state, ptr %state, i32 0, i32 3
          %epoch = load atomic WORD, ptr %epochAddress acquire, align ALIGN
          %current = icmp eq WORD %epoch, %expected
          br i1 %current, label %request, label %end
        request:
          %gate = getelementptr %xe.resume.state, ptr %state, i32 0, i32 2
          %before = atomicrmw or ptr %gate, i32 2 acq_rel
          %idle = icmp eq i32 %before, 0
          br i1 %idle, label %claim, label %end
        claim:
          %claimed = cmpxchg ptr %gate, i32 2, i32 1 acq_rel acquire
          %owner = extractvalue { i32, i1 } %claimed, 1
          br i1 %owner, label %run, label %end
        run:
          %address = getelementptr %xe.resume.state, ptr %state, i32 0, i32 1
          %handle = load ptr, ptr %address
          call void @llvm.coro.resume(ptr %handle)
          call void @__xenon_resume_drain(ptr %state)
          br label %end
        end:
          ret void
        }

        define linkonce_odr { ptr, ptr } @__xenon_resume_continuation(ptr %state) {
        entry:
          %epochAddress = getelementptr %xe.resume.state, ptr %state, i32 0, i32 3
          %oldEpoch = atomicrmw add ptr %epochAddress, WORD 1 acq_rel
          %epoch = add WORD %oldEpoch, 1
          %retain = atomicrmw add ptr %state, WORD 1 monotonic
          %token = call ptr @__xenon_malloc(WORD ptrtoint (ptr getelementptr (%xe.resume.token, ptr null, i32 1) to WORD))
          store ptr %state, ptr %token
          %epochSlot = getelementptr %xe.resume.token, ptr %token, i32 0, i32 1
          store WORD %epoch, ptr %epochSlot
          %used = getelementptr %xe.resume.token, ptr %token, i32 0, i32 2
          store i32 0, ptr %used
          %control = call ptr @__xenon_malloc(WORD ptrtoint (ptr getelementptr (%xe.function.control, ptr null, i32 1) to WORD))
          store WORD 1, ptr %control
          %environment = getelementptr %xe.function.control, ptr %control, i32 0, i32 1
          store ptr %token, ptr %environment
          %destructor = getelementptr %xe.function.control, ptr %control, i32 0, i32 2
          store ptr @__xenon_resume_token_drop, ptr %destructor
          %value = insertvalue { ptr, ptr } { ptr @__xenon_resume_request, ptr null }, ptr %control, 1
          ret { ptr, ptr } %value
        }
        """;
}
