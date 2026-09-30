using LLVMSharp.Interop;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
namespace Xenon.CodeGen.LLVM;
public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private LLVMValueRef EmitBaseAddress(LLVMValueRef address, StructTypeSymbol type, StructTypeSymbol target)
        {
            while (!TypeIdentity.AreSame(type, target))
            {
                if (type.BaseType is not { } parent) throw new LlvmCodeGenerationException("Invalid MIR base projection.");
                address = _builder.BuildStructGEP2(_mapType(type), address, 0, "base.address");
                type = parent;
            }
            return address;
        }
        private static SyntaxKind MirOperator(MirBinaryOperator op) => op switch
        {
            MirBinaryOperator.Add => SyntaxKind.PlusToken, MirBinaryOperator.Subtract => SyntaxKind.MinusToken,
            MirBinaryOperator.Multiply => SyntaxKind.StarToken, MirBinaryOperator.Divide => SyntaxKind.SlashToken,
            MirBinaryOperator.Remainder => SyntaxKind.PercentToken, MirBinaryOperator.BitwiseAnd => SyntaxKind.AmpersandToken,
            MirBinaryOperator.BitwiseOr => SyntaxKind.PipeToken, MirBinaryOperator.BitwiseXor => SyntaxKind.CaretToken,
            MirBinaryOperator.ShiftLeft => SyntaxKind.LessLessToken, MirBinaryOperator.ShiftRight => SyntaxKind.GreaterGreaterToken,
            MirBinaryOperator.Equal => SyntaxKind.EqualsEqualsToken, MirBinaryOperator.NotEqual => SyntaxKind.BangEqualsToken,
            MirBinaryOperator.Less => SyntaxKind.LessToken, MirBinaryOperator.LessOrEqual => SyntaxKind.LessOrEqualsToken,
            MirBinaryOperator.Greater => SyntaxKind.GreaterToken, MirBinaryOperator.GreaterOrEqual => SyntaxKind.GreaterOrEqualsToken,
            _ => throw new LlvmCodeGenerationException("Invalid MIR binary operator."),
        };
        private LLVMValueRef MirBinaryValue(MirBinaryOperator op, TypeSymbol leftType, LLVMValueRef left, TypeSymbol rightType, LLVMValueRef right) =>
            op is >= MirBinaryOperator.Equal and <= MirBinaryOperator.GreaterOrEqual
                ? MirComparison(MirOperator(op), leftType, left, right)
                : EmitArithmetic(MirOperator(op), leftType, left, right, rightType);
        private LLVMValueRef MirConvert(LLVMValueRef value, TypeSymbol sourceType, TypeSymbol targetType)
        {

            if (sourceType is AtomicTypeSymbol atomic &&
                TypeIdentity.AreSame(atomic.ElementType, targetType))
                return value;
            if (TypeIdentity.AreSame(sourceType, targetType))
                return value;
            if (sourceType is PointerTypeSymbol && targetType is PointerTypeSymbol)
                return value;

            if (sourceType is ReferenceTypeSymbol sourceReference && targetType is ReferenceTypeSymbol targetReference)
            {
                if (sourceReference.ElementType is InterfaceTypeSymbol sourceInterface && targetReference.ElementType is InterfaceTypeSymbol targetInterface)
                    {
                    LLVMValueRef view = _builder.BuildLoad2(_mapType(sourceInterface), value, "interface.upcast.source");
                    LLVMValueRef data = _builder.BuildExtractValue(view, 0, "interface.data");
                    LLVMValueRef table = _builder.BuildExtractValue(view, 1, "interface.table");
                    LLVMValueRef map = _builder.BuildLoad2(ResumePointer, table, "interface.map");
                    LLVMValueRef targetTable = EmitInterfaceTableLookup(map, targetInterface);
                    LLVMValueRef converted = _builder.BuildInsertValue(_builder.BuildInsertValue(_mapType(targetInterface).Poison, data, 0), targetTable, 1);
                    LLVMValueRef slot = _builder.BuildAlloca(_mapType(targetInterface), "interface.upcast");
                    _builder.BuildStore(converted, slot);
                    return slot;
                }
                return value;
            }
            if (sourceType is StructTypeSymbol structure && targetType is StructTypeSymbol baseType)
                return ExtractBaseValue(value, structure, baseType);
            if (value.TypeOf == _mapType(targetType)) return value;
            LLVMTypeRef target = _mapType(targetType);
            bool sourceInteger = sourceType is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsCharacter: true } or EnumTypeSymbol;
            bool targetInteger = targetType is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsCharacter: true } or EnumTypeSymbol;
            bool sourceFloat = sourceType is PrimitiveTypeSymbol { IsFloatingPoint: true };
            bool targetFloat = targetType is PrimitiveTypeSymbol { IsFloatingPoint: true };
            if (sourceInteger && targetInteger)
            {
                int sourceWidth = _getIntegerBitWidth(sourceType);
                int targetWidth = _getIntegerBitWidth(targetType);
                if (sourceWidth == targetWidth)
                    return value;
                if (sourceWidth > targetWidth)
                    return _builder.BuildTrunc(value, target, "cast.trunc");
                bool signed = sourceType is PrimitiveTypeSymbol { IsSigned: true } or EnumTypeSymbol { UnderlyingType.IsSigned: true };
                return signed
                    ? _builder.BuildSExt(value, target, "cast.sext")
                    : _builder.BuildZExt(value, target, "cast.zext");
            }
            if (sourceInteger && targetFloat)
            {
                bool signed = sourceType is PrimitiveTypeSymbol { IsSigned: true };
                return signed
                    ? _builder.BuildSIToFP(value, target, "cast.sitofp")
                    : _builder.BuildUIToFP(value, target, "cast.uitofp");
            }
            if (sourceFloat && targetInteger)
            {
                bool signed = targetType is PrimitiveTypeSymbol { IsSigned: true };
                int width = _getIntegerBitWidth(targetType);
                double upper = Math.ScaleB(1.0, signed ? width - 1 : width);
                double minimum = signed ? -upper : 0;
                double lower = minimum - 1;
                if (TypeIdentity.AreSame(sourceType, BuiltinTypes.Float)) lower = (float)lower;
                // Truncation permits fractions immediately below the minimum.
                // At large widths min-1 rounds to min: use an inclusive bound then.
                LLVMValueRef aboveMinimum = _builder.BuildFCmp(lower < minimum ? LLVMRealPredicate.LLVMRealOGT : LLVMRealPredicate.LLVMRealOGE,
                    value, LLVMValueRef.CreateConstReal(value.TypeOf, lower), "cast.lower.valid");
                LLVMValueRef belowMaximum = _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT,
                    value, LLVMValueRef.CreateConstReal(value.TypeOf, upper), "cast.upper.valid");
                EmitRuntimeCheck(_builder.BuildAnd(aboveMinimum, belowMaximum, "cast.range.valid"));
                return signed
                    ? _builder.BuildFPToSI(value, target, "cast.fptosi")
                    : _builder.BuildFPToUI(value, target, "cast.fptoui");
            }
            if (sourceFloat && targetFloat)
            {
                int sourceWidth = ((PrimitiveTypeSymbol)sourceType).BitWidth!.Value;
                int targetWidth = ((PrimitiveTypeSymbol)targetType).BitWidth!.Value;
                return sourceWidth < targetWidth
                    ? _builder.BuildFPExt(value, target, "cast.fpext")
                    : _builder.BuildFPTrunc(value, target, "cast.fptrunc");
            }
            throw new LlvmCodeGenerationException($"cast from '{sourceType.Name}' to '{targetType.Name}' is not supported");
        }

        private LLVMValueRef MirComparison(
            SyntaxKind operatorKind, TypeSymbol operandType,
            LLVMValueRef left,
            LLVMValueRef right)
        {
            if (operatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
            {
                LLVMValueRef equal = EmitValueEquality(left, right, operandType);
                return operatorKind == SyntaxKind.EqualsEqualsToken
                    ? equal
                    : _builder.BuildNot(equal, "value.not.equal");
            }

            if (operandType is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                LLVMRealPredicate realPredicate = operatorKind switch
                {
                    SyntaxKind.LessToken => LLVMRealPredicate.LLVMRealOLT,
                    SyntaxKind.LessOrEqualsToken => LLVMRealPredicate.LLVMRealOLE,
                    SyntaxKind.GreaterToken => LLVMRealPredicate.LLVMRealOGT,
                    SyntaxKind.GreaterOrEqualsToken => LLVMRealPredicate.LLVMRealOGE,
                    _ => throw new LlvmCodeGenerationException("Invalid floating-point comparison."),
                };
                return _builder.BuildFCmp(realPredicate, left, right, "fcmp");
            }

            bool signed = operandType is PrimitiveTypeSymbol { IsSigned: true };
            LLVMIntPredicate intPredicate = operatorKind switch
            {
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


    }
}
