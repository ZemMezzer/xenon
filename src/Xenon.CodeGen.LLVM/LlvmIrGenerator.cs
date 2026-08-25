using System.Collections.Immutable;
using LLVMSharp.Interop;
using Xenon.Compiler;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

public sealed class LlvmIrGenerator
{
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions = [];
    private LLVMContextRef _context;
    private LLVMModuleRef _module;
    private NativeTargetMachine? _targetMachine;

    public string Generate(Compilation compilation, string moduleName = "xenon") =>
        GenerateModule(
            compilation,
            moduleName,
            targetMachine: null,
            module => module.PrintToString(),
            generateExecutableEntryPoint: false);

    public string GenerateForTarget(
        Compilation compilation,
        LlvmTargetOptions targetOptions,
        string moduleName = "xenon",
        bool generateExecutableEntryPoint = false)
    {
        ArgumentNullException.ThrowIfNull(targetOptions);
        using NativeTargetMachine targetMachine = NativeTargetMachine.Create(targetOptions);
        return GenerateModule(
            compilation,
            moduleName,
            targetMachine,
            module => module.PrintToString(),
            generateExecutableEntryPoint);
    }

    internal TResult GenerateModule<TResult>(
        Compilation compilation,
        string moduleName,
        NativeTargetMachine? targetMachine,
        Func<LLVMModuleRef, TResult> resultFactory,
        bool generateExecutableEntryPoint)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(resultFactory);

        if (compilation.HasErrors)
        {
            throw new LlvmCodeGenerationException("LLVM IR cannot be generated while the compilation contains errors.");
        }

        _context = LLVMContextRef.Create();
        _module = _context.CreateModuleWithName(moduleName);
        _targetMachine = targetMachine;

        try
        {
            if (targetMachine is not null)
            {
                _module.Target = targetMachine.Triple;
                _module.DataLayout = targetMachine.DataLayout;
            }

            DeclareFunctions(compilation.SemanticModel.GlobalNamespace);
            EmitFunctionBodies(compilation.SemanticModel.Functions);
            if (generateExecutableEntryPoint)
            {
                EmitExecutableEntryPoint(compilation.SemanticModel.Functions);
            }

            _module.Verify(LLVMVerifierFailureAction.LLVMReturnStatusAction);
            return resultFactory(_module);
        }
        catch (LlvmCodeGenerationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LlvmCodeGenerationException("LLVM failed to generate or verify the module.", exception);
        }
        finally
        {
            _module.Dispose();
            _context.Dispose();
            _functions.Clear();
            _targetMachine = null;
        }
    }

    private void DeclareFunctions(NamespaceSymbol @namespace)
    {
        foreach (FunctionSymbol function in @namespace.Functions)
        {
            LLVMTypeRef returnType = MapType(function.ReturnType);
            LLVMTypeRef[] parameterTypes = function.Parameters
                .Select(parameter => MapType(parameter.Type))
                .ToArray();
            LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(returnType, parameterTypes, false);
            LLVMValueRef value = _module.AddFunction(NativeSymbolNames.Get(function), functionType);
            if (!function.IsExtern && !function.IsExport)
            {
                value.Linkage = LLVMLinkage.LLVMInternalLinkage;
            }
            else if (function.IsExport && IsWindowsTarget())
            {
                value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
            }

            _functions.Add(function, new LlvmFunction(value, functionType));
        }

        foreach (NamespaceSymbol child in @namespace.Namespaces)
        {
            DeclareFunctions(child);
        }
    }

    private void EmitFunctionBodies(ImmutableArray<BoundFunction> functions)
    {
        foreach (BoundFunction function in functions)
        {
            LlvmFunction declaration = _functions[function.Symbol];
            using LLVMBuilderRef builder = _context.CreateBuilder();
            LLVMBasicBlockRef entry = declaration.Value.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);

            var emitter = new FunctionEmitter(
                _context,
                builder,
                function.Symbol,
                declaration.Value,
                _functions,
                MapType);
            emitter.Emit(function.Body);
        }
    }

    private void EmitExecutableEntryPoint(ImmutableArray<BoundFunction> functions)
    {
        BoundFunction[] candidates = functions
            .Where(function => function.Symbol.Name == "Main")
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new LlvmCodeGenerationException(
                "Executable project must declare exactly one entry point 'int Main()'.");
        }

        if (candidates.Length > 1)
        {
            throw new LlvmCodeGenerationException(
                "Executable project declares multiple functions named 'Main'.");
        }

        FunctionSymbol entryPoint = candidates[0].Symbol;
        if (!ReferenceEquals(entryPoint.ReturnType, BuiltinTypes.Int) || !entryPoint.Parameters.IsEmpty)
        {
            throw new LlvmCodeGenerationException(
                $"Entry point '{entryPoint.FullName}' must have signature 'int Main()'.");
        }

        LlvmFunction xenonEntryPoint = _functions[entryPoint];
        LLVMTypeRef nativeEntryPointType = LLVMTypeRef.CreateFunction(_context.Int32Type, [], false);
        if (_module.GetNamedFunction("main").Handle != IntPtr.Zero)
        {
            throw new LlvmCodeGenerationException(
                "Native symbol 'main' conflicts with the generated executable entry point.");
        }

        LLVMValueRef nativeEntryPoint = _module.AddFunction("main", nativeEntryPointType);
        LLVMBasicBlockRef block = nativeEntryPoint.AppendBasicBlock("entry");
        using LLVMBuilderRef builder = _context.CreateBuilder();
        builder.PositionAtEnd(block);
        LLVMValueRef result = builder.BuildCall2(
            xenonEntryPoint.Type,
            xenonEntryPoint.Value,
            Array.Empty<LLVMValueRef>(),
            "result");
        builder.BuildRet(result);
    }

    private LLVMTypeRef MapType(TypeSymbol type)
    {
        if (ReferenceEquals(type, BuiltinTypes.Void))
        {
            return _context.VoidType;
        }

        if (ReferenceEquals(type, BuiltinTypes.Bool))
        {
            return _context.Int1Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Byte) || ReferenceEquals(type, BuiltinTypes.SByte))
        {
            return _context.Int8Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Short) || ReferenceEquals(type, BuiltinTypes.UShort))
        {
            return _context.Int16Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Int) || ReferenceEquals(type, BuiltinTypes.UInt))
        {
            return _context.Int32Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Long) || ReferenceEquals(type, BuiltinTypes.ULong))
        {
            return _context.Int64Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.NInt) || ReferenceEquals(type, BuiltinTypes.NUInt))
        {
            return MapTargetInteger(type, GetPointerBitWidth());
        }

        if (ReferenceEquals(type, BuiltinTypes.CLong) || ReferenceEquals(type, BuiltinTypes.CULong))
        {
            int bitWidth = IsWindowsTarget() ? 32 : GetPointerBitWidth();
            return MapTargetInteger(type, bitWidth);
        }

        if (ReferenceEquals(type, BuiltinTypes.Float))
        {
            return _context.FloatType;
        }

        if (ReferenceEquals(type, BuiltinTypes.Double))
        {
            return _context.DoubleType;
        }

        if (type is PointerTypeSymbol pointer)
        {
            return LLVMTypeRef.CreatePointer(MapType(pointer.ElementType), 0);
        }

        throw new LlvmCodeGenerationException(
            $"Type '{type.Name}' requires target information and cannot be lowered by the target-independent LLVM milestone.");
    }

    private LLVMTypeRef MapTargetInteger(TypeSymbol type, int bitWidth) => bitWidth switch
    {
        32 => _context.Int32Type,
        64 => _context.Int64Type,
        _ => throw new LlvmCodeGenerationException(
            $"Target integer type '{type.Name}' has unsupported width {bitWidth}."),
    };

    private int GetPointerBitWidth() => _targetMachine?.PointerBitWidth
        ?? throw new LlvmCodeGenerationException(
            "Target-dependent integer types require a configured LLVM target machine.");

    private bool IsWindowsTarget() => _targetMachine?.Triple.Contains(
        "windows",
        StringComparison.OrdinalIgnoreCase) is true ||
        _targetMachine?.Triple.Contains("win32", StringComparison.OrdinalIgnoreCase) is true;

    private readonly record struct LlvmFunction(LLVMValueRef Value, LLVMTypeRef Type);

    private sealed class FunctionEmitter
    {
        private readonly LLVMContextRef _context;
        private readonly LLVMBuilderRef _builder;
        private readonly FunctionSymbol _function;
        private readonly LLVMValueRef _llvmFunction;
        private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions;
        private readonly Func<TypeSymbol, LLVMTypeRef> _mapType;
        private readonly Dictionary<VariableSymbol, LLVMValueRef> _addresses = [];
        private readonly Stack<LoopTargets> _loopTargets = [];
        private bool _terminated;

        public FunctionEmitter(
            LLVMContextRef context,
            LLVMBuilderRef builder,
            FunctionSymbol function,
            LLVMValueRef llvmFunction,
            Dictionary<FunctionSymbol, LlvmFunction> functions,
            Func<TypeSymbol, LLVMTypeRef> mapType)
        {
            _context = context;
            _builder = builder;
            _function = function;
            _llvmFunction = llvmFunction;
            _functions = functions;
            _mapType = mapType;

            for (int index = 0; index < function.Parameters.Length; index++)
            {
                ParameterSymbol parameter = function.Parameters[index];
                LLVMValueRef address = _builder.BuildAlloca(_mapType(parameter.Type), parameter.Name);
                _builder.BuildStore(llvmFunction.GetParam((uint)index), address);
                _addresses.Add(parameter, address);
            }
        }

        public void Emit(BoundBlockStatement body)
        {
            AllocateLocals(body);
            EmitBlock(body);

            if (!_terminated && ReferenceEquals(_function.ReturnType, BuiltinTypes.Void))
            {
                _builder.BuildRetVoid();
                _terminated = true;
            }
        }

        private void AllocateLocals(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (BoundStatement child in block.Statements)
                    {
                        AllocateLocals(child);
                    }

                    break;
                case BoundVariableDeclarationStatement variable:
                    LLVMValueRef address = _builder.BuildAlloca(_mapType(variable.Variable.Type), variable.Variable.Name);
                    _addresses.Add(variable.Variable, address);
                    break;
                case BoundIfStatement @if:
                    AllocateLocals(@if.ThenStatement);
                    if (@if.ElseStatement is not null)
                    {
                        AllocateLocals(@if.ElseStatement);
                    }

                    break;
                case BoundWhileStatement @while:
                    AllocateLocals(@while.Body);
                    break;
                case BoundForStatement @for:
                    if (@for.Initializer is not null)
                    {
                        AllocateLocals(@for.Initializer);
                    }

                    AllocateLocals(@for.Body);
                    break;
            }
        }

        private void EmitBlock(BoundBlockStatement block)
        {
            foreach (BoundStatement statement in block.Statements)
            {
                if (_terminated)
                {
                    break;
                }

                EmitStatement(statement);
            }
        }

        private void EmitStatement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    EmitBlock(block);
                    break;
                case BoundVariableDeclarationStatement variable:
                    EmitVariableDeclaration(variable);
                    break;
                case BoundExpressionStatement expression:
                    EmitExpression(expression.Expression);
                    break;
                case BoundReturnStatement @return:
                    EmitReturn(@return);
                    break;
                case BoundIfStatement @if:
                    EmitIf(@if);
                    break;
                case BoundWhileStatement @while:
                    EmitWhile(@while);
                    break;
                case BoundForStatement @for:
                    EmitFor(@for);
                    break;
                case BoundBreakStatement:
                    EmitLoopBranch(_loopTargets.Peek().BreakTarget);
                    break;
                case BoundContinueStatement:
                    EmitLoopBranch(_loopTargets.Peek().ContinueTarget);
                    break;
                default:
                    throw new LlvmCodeGenerationException($"Bound statement '{statement.Kind}' is not supported by LLVM code generation.");
            }
        }

        private void EmitIf(BoundIfStatement statement)
        {
            LLVMValueRef condition = EmitExpression(statement.Condition);
            LLVMBasicBlockRef thenBlock = _llvmFunction.AppendBasicBlock("if.then");

            if (statement.ElseStatement is null)
            {
                LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("if.end");
                _builder.BuildCondBr(condition, thenBlock, endBlock);

                _builder.PositionAtEnd(thenBlock);
                _terminated = false;
                EmitStatement(statement.ThenStatement);
                if (!_terminated)
                {
                    _builder.BuildBr(endBlock);
                }

                _builder.PositionAtEnd(endBlock);
                _terminated = false;
                return;
            }

            LLVMBasicBlockRef elseBlock = _llvmFunction.AppendBasicBlock("if.else");
            _builder.BuildCondBr(condition, thenBlock, elseBlock);

            _builder.PositionAtEnd(thenBlock);
            _terminated = false;
            EmitStatement(statement.ThenStatement);
            bool thenFallsThrough = !_terminated;
            LLVMBasicBlockRef thenEnd = _builder.InsertBlock;

            _builder.PositionAtEnd(elseBlock);
            _terminated = false;
            EmitStatement(statement.ElseStatement);
            bool elseFallsThrough = !_terminated;
            LLVMBasicBlockRef elseEnd = _builder.InsertBlock;

            if (!thenFallsThrough && !elseFallsThrough)
            {
                _terminated = true;
                return;
            }

            LLVMBasicBlockRef mergeBlock = _llvmFunction.AppendBasicBlock("if.end");
            if (thenFallsThrough)
            {
                _builder.PositionAtEnd(thenEnd);
                _builder.BuildBr(mergeBlock);
            }

            if (elseFallsThrough)
            {
                _builder.PositionAtEnd(elseEnd);
                _builder.BuildBr(mergeBlock);
            }

            _builder.PositionAtEnd(mergeBlock);
            _terminated = false;
        }

        private void EmitWhile(BoundWhileStatement statement)
        {
            LLVMBasicBlockRef conditionBlock = _llvmFunction.AppendBasicBlock("while.condition");
            LLVMBasicBlockRef bodyBlock = _llvmFunction.AppendBasicBlock("while.body");
            LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("while.end");
            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(conditionBlock);
            LLVMValueRef condition = EmitExpression(statement.Condition);
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            _builder.PositionAtEnd(bodyBlock);
            _terminated = false;
            _loopTargets.Push(new LoopTargets(endBlock, conditionBlock));
            EmitStatement(statement.Body);
            _loopTargets.Pop();
            if (!_terminated)
            {
                _builder.BuildBr(conditionBlock);
            }

            _builder.PositionAtEnd(endBlock);
            _terminated = false;
        }

        private void EmitFor(BoundForStatement statement)
        {
            if (statement.Initializer is not null)
            {
                EmitStatement(statement.Initializer);
            }

            LLVMBasicBlockRef conditionBlock = _llvmFunction.AppendBasicBlock("for.condition");
            LLVMBasicBlockRef bodyBlock = _llvmFunction.AppendBasicBlock("for.body");
            LLVMBasicBlockRef incrementBlock = _llvmFunction.AppendBasicBlock("for.increment");
            LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("for.end");
            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(conditionBlock);
            LLVMValueRef condition = statement.Condition is null
                ? LLVMValueRef.CreateConstInt(_context.Int1Type, 1, false)
                : EmitExpression(statement.Condition);
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            _builder.PositionAtEnd(bodyBlock);
            _terminated = false;
            _loopTargets.Push(new LoopTargets(endBlock, incrementBlock));
            EmitStatement(statement.Body);
            _loopTargets.Pop();
            if (!_terminated)
            {
                _builder.BuildBr(incrementBlock);
            }

            _builder.PositionAtEnd(incrementBlock);
            _terminated = false;
            if (statement.Increment is not null)
            {
                EmitExpression(statement.Increment);
            }

            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(endBlock);
            _terminated = false;
        }

        private void EmitLoopBranch(LLVMBasicBlockRef target)
        {
            _builder.BuildBr(target);
            _terminated = true;
        }

        private void EmitVariableDeclaration(BoundVariableDeclarationStatement statement)
        {
            LLVMValueRef address = GetAddress(statement.Variable);

            if (statement.Initializer is not null)
            {
                _builder.BuildStore(EmitExpression(statement.Initializer), address);
            }
        }

        private void EmitReturn(BoundReturnStatement statement)
        {
            if (statement.Expression is null)
            {
                _builder.BuildRetVoid();
            }
            else
            {
                _builder.BuildRet(EmitExpression(statement.Expression));
            }

            _terminated = true;
        }

        private LLVMValueRef EmitExpression(BoundExpression expression) => expression switch
        {
            BoundLiteralExpression literal => EmitLiteral(literal),
            BoundVariableExpression variable => EmitVariable(variable),
            BoundUnaryExpression unary => EmitUnary(unary),
            BoundBinaryExpression binary => EmitBinary(binary),
            BoundAssignmentExpression assignment => EmitAssignment(assignment),
            BoundCallExpression call => EmitCall(call),
            _ => throw new LlvmCodeGenerationException($"Bound expression '{expression.Kind}' is not supported by LLVM code generation."),
        };

        private LLVMValueRef EmitLiteral(BoundLiteralExpression expression)
        {
            LLVMTypeRef type = _mapType(expression.Type);

            if (ReferenceEquals(expression.Type, BuiltinTypes.Bool))
            {
                return LLVMValueRef.CreateConstInt(type, expression.Value is true ? 1UL : 0UL, false);
            }

            if (expression.Type is PrimitiveTypeSymbol { IsInteger: true })
            {
                ulong value = expression.Value switch
                {
                    int integer => unchecked((ulong)integer),
                    long integer => unchecked((ulong)integer),
                    ulong integer => integer,
                    _ => throw new LlvmCodeGenerationException("Invalid bound integer literal."),
                };
                return LLVMValueRef.CreateConstInt(type, value, true);
            }

            if (expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                double value = expression.Value switch
                {
                    float single => single,
                    double @double => @double,
                    _ => throw new LlvmCodeGenerationException("Invalid bound floating-point literal."),
                };
                return LLVMValueRef.CreateConstReal(type, value);
            }

            if (expression.Value is string text)
            {
                return _builder.BuildGlobalStringPtr(text, "str");
            }

            if (expression.Type is PointerTypeSymbol && expression.Value is null)
            {
                return LLVMValueRef.CreateConstPointerNull(type);
            }

            throw new LlvmCodeGenerationException($"Literal of type '{expression.Type.Name}' is not supported.");
        }

        private LLVMValueRef EmitVariable(BoundVariableExpression expression)
        {
            LLVMValueRef address = GetAddress(expression.Variable);
            return _builder.BuildLoad2(_mapType(expression.Type), address, expression.Variable.Name);
        }

        private LLVMValueRef EmitUnary(BoundUnaryExpression expression)
        {
            if (expression.OperatorKind == SyntaxKind.AmpersandToken && expression.Operand is BoundVariableExpression variable)
            {
                return GetAddress(variable.Variable);
            }

            LLVMValueRef operand = EmitExpression(expression.Operand);
            return expression.OperatorKind switch
            {
                SyntaxKind.PlusToken => operand,
                SyntaxKind.MinusToken when expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true } =>
                    _builder.BuildFNeg(operand, "fneg"),
                SyntaxKind.MinusToken => _builder.BuildNeg(operand, "neg"),
                SyntaxKind.BangToken or SyntaxKind.TildeToken => _builder.BuildNot(operand, "not"),
                SyntaxKind.StarToken when expression.Operand.Type is PointerTypeSymbol pointer =>
                    _builder.BuildLoad2(_mapType(pointer.ElementType), operand, "deref"),
                SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken => EmitIncrement(expression, operand),
                _ => throw new LlvmCodeGenerationException($"Unary operator '{expression.OperatorKind}' is not supported."),
            };
        }

        private LLVMValueRef EmitIncrement(BoundUnaryExpression expression, LLVMValueRef operand)
        {
            if (expression.Operand is not BoundVariableExpression variable)
            {
                throw new LlvmCodeGenerationException("Increment and decrement require a variable.");
            }

            LLVMValueRef one = expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true }
                ? LLVMValueRef.CreateConstReal(_mapType(expression.Type), 1.0)
                : LLVMValueRef.CreateConstInt(_mapType(expression.Type), 1, false);
            LLVMValueRef result = expression.OperatorKind == SyntaxKind.PlusPlusToken
                ? EmitArithmetic(SyntaxKind.PlusToken, expression.Type, operand, one)
                : EmitArithmetic(SyntaxKind.MinusToken, expression.Type, operand, one);
            _builder.BuildStore(result, GetAddress(variable.Variable));
            return expression.IsPostfix ? operand : result;
        }

        private LLVMValueRef EmitBinary(BoundBinaryExpression expression)
        {
            if (expression.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
            {
                return EmitShortCircuit(expression);
            }

            LLVMValueRef left = EmitExpression(expression.Left);
            LLVMValueRef right = EmitExpression(expression.Right);

            if (expression.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
                SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
                SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken)
            {
                return EmitComparison(expression, left, right);
            }

            return EmitArithmetic(expression.OperatorKind, expression.Left.Type, left, right);
        }

        private LLVMValueRef EmitShortCircuit(BoundBinaryExpression expression)
        {
            LLVMValueRef left = EmitExpression(expression.Left);
            LLVMBasicBlockRef leftBlock = _builder.InsertBlock;
            LLVMBasicBlockRef rightBlock = _llvmFunction.AppendBasicBlock("logic.rhs");
            LLVMBasicBlockRef mergeBlock = _llvmFunction.AppendBasicBlock("logic.end");
            bool isAnd = expression.OperatorKind == SyntaxKind.AmpersandAmpersandToken;

            if (isAnd)
            {
                _builder.BuildCondBr(left, rightBlock, mergeBlock);
            }
            else
            {
                _builder.BuildCondBr(left, mergeBlock, rightBlock);
            }

            _builder.PositionAtEnd(rightBlock);
            LLVMValueRef right = EmitExpression(expression.Right);
            LLVMBasicBlockRef rightEnd = _builder.InsertBlock;
            _builder.BuildBr(mergeBlock);

            _builder.PositionAtEnd(mergeBlock);
            LLVMValueRef shortCircuitValue = LLVMValueRef.CreateConstInt(
                _context.Int1Type,
                isAnd ? 0UL : 1UL,
                false);
            LLVMValueRef result = _builder.BuildPhi(_context.Int1Type, "logic");
            result.AddIncoming(
                [shortCircuitValue, right],
                [leftBlock, rightEnd],
                2);
            return result;
        }

        private LLVMValueRef EmitArithmetic(
            SyntaxKind operatorKind,
            TypeSymbol operandType,
            LLVMValueRef left,
            LLVMValueRef right)
        {
            if (operandType is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                return operatorKind switch
                {
                    SyntaxKind.PlusToken => _builder.BuildFAdd(left, right, "fadd"),
                    SyntaxKind.MinusToken => _builder.BuildFSub(left, right, "fsub"),
                    SyntaxKind.StarToken => _builder.BuildFMul(left, right, "fmul"),
                    SyntaxKind.SlashToken => _builder.BuildFDiv(left, right, "fdiv"),
                    SyntaxKind.PercentToken => _builder.BuildFRem(left, right, "frem"),
                    _ => throw new LlvmCodeGenerationException($"Floating-point operator '{operatorKind}' is not supported."),
                };
            }

            bool signed = operandType is PrimitiveTypeSymbol { IsSigned: true };
            return operatorKind switch
            {
                SyntaxKind.PlusToken => _builder.BuildAdd(left, right, "add"),
                SyntaxKind.MinusToken => _builder.BuildSub(left, right, "sub"),
                SyntaxKind.StarToken => _builder.BuildMul(left, right, "mul"),
                SyntaxKind.SlashToken when signed => _builder.BuildSDiv(left, right, "sdiv"),
                SyntaxKind.SlashToken => _builder.BuildUDiv(left, right, "udiv"),
                SyntaxKind.PercentToken when signed => _builder.BuildSRem(left, right, "srem"),
                SyntaxKind.PercentToken => _builder.BuildURem(left, right, "urem"),
                SyntaxKind.AmpersandToken => _builder.BuildAnd(left, right, "and"),
                SyntaxKind.PipeToken => _builder.BuildOr(left, right, "or"),
                SyntaxKind.CaretToken => _builder.BuildXor(left, right, "xor"),
                SyntaxKind.LessLessToken => _builder.BuildShl(left, right, "shl"),
                SyntaxKind.GreaterGreaterToken when signed => _builder.BuildAShr(left, right, "ashr"),
                SyntaxKind.GreaterGreaterToken => _builder.BuildLShr(left, right, "lshr"),
                _ => throw new LlvmCodeGenerationException($"Integer operator '{operatorKind}' is not supported."),
            };
        }

        private LLVMValueRef EmitComparison(
            BoundBinaryExpression expression,
            LLVMValueRef left,
            LLVMValueRef right)
        {
            if (expression.Left.Type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                LLVMRealPredicate realPredicate = expression.OperatorKind switch
                {
                    SyntaxKind.EqualsEqualsToken => LLVMRealPredicate.LLVMRealOEQ,
                    SyntaxKind.BangEqualsToken => LLVMRealPredicate.LLVMRealONE,
                    SyntaxKind.LessToken => LLVMRealPredicate.LLVMRealOLT,
                    SyntaxKind.LessOrEqualsToken => LLVMRealPredicate.LLVMRealOLE,
                    SyntaxKind.GreaterToken => LLVMRealPredicate.LLVMRealOGT,
                    SyntaxKind.GreaterOrEqualsToken => LLVMRealPredicate.LLVMRealOGE,
                    _ => throw new LlvmCodeGenerationException("Invalid floating-point comparison."),
                };
                return _builder.BuildFCmp(realPredicate, left, right, "fcmp");
            }

            bool signed = expression.Left.Type is PrimitiveTypeSymbol { IsSigned: true };
            LLVMIntPredicate intPredicate = expression.OperatorKind switch
            {
                SyntaxKind.EqualsEqualsToken => LLVMIntPredicate.LLVMIntEQ,
                SyntaxKind.BangEqualsToken => LLVMIntPredicate.LLVMIntNE,
                SyntaxKind.LessToken when signed => LLVMIntPredicate.LLVMIntSLT,
                SyntaxKind.LessToken => LLVMIntPredicate.LLVMIntULT,
                SyntaxKind.LessOrEqualsToken when signed => LLVMIntPredicate.LLVMIntSLE,
                SyntaxKind.LessOrEqualsToken => LLVMIntPredicate.LLVMIntULE,
                SyntaxKind.GreaterToken when signed => LLVMIntPredicate.LLVMIntSGT,
                SyntaxKind.GreaterToken => LLVMIntPredicate.LLVMIntUGT,
                SyntaxKind.GreaterOrEqualsToken when signed => LLVMIntPredicate.LLVMIntSGE,
                SyntaxKind.GreaterOrEqualsToken => LLVMIntPredicate.LLVMIntUGE,
                _ => throw new LlvmCodeGenerationException("Invalid integer comparison."),
            };
            return _builder.BuildICmp(intPredicate, left, right, "icmp");
        }

        private LLVMValueRef EmitAssignment(BoundAssignmentExpression expression)
        {
            LLVMValueRef value = EmitExpression(expression.Expression);
            LLVMValueRef address = GetAddress(expression.Variable);

            if (expression.OperatorKind != SyntaxKind.EqualsToken)
            {
                LLVMValueRef current = _builder.BuildLoad2(_mapType(expression.Variable.Type), address, expression.Variable.Name);
                value = EmitArithmetic(
                    GetBinaryOperatorForCompoundAssignment(expression.OperatorKind),
                    expression.Variable.Type,
                    current,
                    value);
            }

            _builder.BuildStore(value, address);
            return value;
        }

        private LLVMValueRef EmitCall(BoundCallExpression expression)
        {
            LlvmFunction function = _functions[expression.Function];
            LLVMValueRef[] arguments = expression.Arguments.Select(EmitExpression).ToArray();
            string name = ReferenceEquals(expression.Type, BuiltinTypes.Void) ? string.Empty : "call";
            return _builder.BuildCall2(function.Type, function.Value, arguments, name);
        }

        private LLVMValueRef GetAddress(VariableSymbol variable)
        {
            if (!_addresses.TryGetValue(variable, out LLVMValueRef address))
            {
                throw new LlvmCodeGenerationException($"No storage was allocated for variable '{variable.Name}'.");
            }

            return address;
        }

        private static SyntaxKind GetBinaryOperatorForCompoundAssignment(SyntaxKind kind) => kind switch
        {
            SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
            SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
            SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
            SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
            SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
            SyntaxKind.AmpersandEqualsToken => SyntaxKind.AmpersandToken,
            SyntaxKind.PipeEqualsToken => SyntaxKind.PipeToken,
            SyntaxKind.CaretEqualsToken => SyntaxKind.CaretToken,
            SyntaxKind.LessLessEqualsToken => SyntaxKind.LessLessToken,
            SyntaxKind.GreaterGreaterEqualsToken => SyntaxKind.GreaterGreaterToken,
            _ => throw new LlvmCodeGenerationException($"Invalid compound assignment operator '{kind}'."),
        };

        private readonly record struct LoopTargets(
            LLVMBasicBlockRef BreakTarget,
            LLVMBasicBlockRef ContinueTarget);
    }
}
