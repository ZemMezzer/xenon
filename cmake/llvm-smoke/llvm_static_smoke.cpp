#include <llvm-c/Core.h>

int main()
{
    LLVMContextRef context = LLVMContextCreate();
    if (context == nullptr)
        return 1;
    LLVMContextDispose(context);
    return 0;
}
