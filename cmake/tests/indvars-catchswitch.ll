; RUN: opt -passes="function(loop-simplify,lcssa,loop(indvars))" -verify-each -disable-output %s
; An i32 destruction cursor must survive Windows EH when LLVM widens indexing.
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"
define void @destroy(ptr %data, i32 %count) personality ptr @__CxxFrameHandler3 {
entry:
  br label %loop
loop:
  %remaining = phi i32 [ %count, %entry ], [ %next, %body ]
  %more = icmp sgt i32 %remaining, 0
  br i1 %more, label %body, label %exit
body:
  %next = add nsw i32 %remaining, -1
  %index = zext nneg i32 %next to i64
  %element = getelementptr i64, ptr %data, i64 %index
  invoke void @destruct(ptr %element) to label %loop unwind label %catchswitch
catchswitch:
  %saved = phi i32 [ %next, %body ]
  %switch = catchswitch within none [label %catchpad] unwind to caller
catchpad:
  %pad = catchpad within %switch [ptr null, i32 64, ptr null]
  catchret from %pad to label %caught
caught:
  call void @cleanup(ptr %data, i32 %saved)
  ret void
exit:
  ret void
}
declare i32 @__CxxFrameHandler3(...)
declare void @destruct(ptr)
declare void @cleanup(ptr, i32)
