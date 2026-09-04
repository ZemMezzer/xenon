using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

internal sealed class Parser
{
    private readonly List<SyntaxToken> _tokens;
    private int _position;

    public Parser(ImmutableArray<SyntaxToken> tokens)
    {
        _tokens = tokens.Where(token => token.Kind != SyntaxKind.BadToken).ToList();
    }

    public DiagnosticBag Diagnostics { get; } = new();

    private SyntaxToken Current => Peek(0);

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var usings = ImmutableArray.CreateBuilder<UsingDirectiveSyntax>();
        while (Current.Kind == SyntaxKind.UsingKeyword)
        {
            usings.Add(ParseUsingDirective());
        }

        NamespaceDeclarationSyntax @namespace = ParseNamespaceDeclaration();
        var members = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();

        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            int start = _position;

            if (Current.Kind == SyntaxKind.UsingKeyword)
            {
                Diagnostics.Report(Current.Location, "using directives must appear before the namespace declaration",
                    DiagnosticIds.UsingDirectiveOrder);
                ParseUsingDirective();
            }
            else
            {
                members.Add(Current.Kind switch
                {
                    SyntaxKind.StructKeyword or SyntaxKind.AbstractKeyword => ParseStructDeclaration(),
                    SyntaxKind.EnumKeyword => ParseEnumDeclaration(),
                    SyntaxKind.InterfaceKeyword => ParseInterfaceDeclaration(),
                    SyntaxKind.TemplateKeyword => ParseTemplateDeclaration(),
                    SyntaxKind.ConstKeyword => ParseModuleConstantDeclaration(),
                    _ => ParseFunctionDeclaration(),
                });
            }

            if (_position == start)
            {
                NextToken();
            }
        }

        SyntaxToken endOfFile = MatchToken(SyntaxKind.EndOfFileToken);
        return new CompilationUnitSyntax(usings.ToImmutable(), @namespace, members.ToImmutable(), endOfFile);
    }

    private UsingDirectiveSyntax ParseUsingDirective()
    {
        SyntaxToken usingKeyword = MatchToken(SyntaxKind.UsingKeyword);
        SyntaxToken? alias = null;
        SyntaxToken? equals = null;

        if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.EqualsToken)
        {
            alias = NextToken();
            equals = MatchToken(SyntaxKind.EqualsToken);
        }

        var nameParts = ImmutableArray.CreateBuilder<SyntaxToken>();
        var dotTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        while (Current.Kind == SyntaxKind.DotToken)
        {
            dotTokens.Add(NextToken());
            nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        }

        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new UsingDirectiveSyntax(
            usingKeyword,
            alias,
            equals,
            nameParts.ToImmutable(),
            dotTokens.ToImmutable(),
            semicolon);
    }

    private EnumDeclarationSyntax ParseEnumDeclaration()
    {
        SyntaxToken keyword = NextToken();
        SyntaxToken name = MatchToken(SyntaxKind.IdentifierToken);
        TypeSyntax? underlying = null;
        if (Current.Kind == SyntaxKind.ColonToken)
        {
            NextToken();
            underlying = ParseType();
        }
        MatchToken(SyntaxKind.OpenBraceToken);
        var members = ImmutableArray.CreateBuilder<EnumMemberDeclarationSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            SyntaxToken member = MatchToken(SyntaxKind.IdentifierToken);
            ExpressionSyntax? value = null;
            if (Current.Kind == SyntaxKind.EqualsToken)
            {
                NextToken();
                value = ParseExpression();
            }
            members.Add(new EnumMemberDeclarationSyntax(member, value));
            if (Current.Kind == SyntaxKind.CloseBraceToken) break;
            MatchToken(SyntaxKind.CommaToken);
            if (_position == start) NextToken();
        }
        MatchToken(SyntaxKind.CloseBraceToken);
        return new EnumDeclarationSyntax(keyword, name, underlying, members.ToImmutable());
    }

    private StructDeclarationSyntax ParseStructDeclaration()
    {
        SyntaxToken? abstractKeyword = null;
        while (Current.Kind == SyntaxKind.AbstractKeyword)
        {
            SyntaxToken modifier = NextToken();
            if (abstractKeyword is not null) Diagnostics.Report(modifier.Location, "duplicate abstract struct modifier",
                DiagnosticIds.DuplicateModifier);
            abstractKeyword ??= modifier;
        }
        SyntaxToken structKeyword = MatchToken(SyntaxKind.StructKeyword);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        GenericParameterListSyntax? typeParameters = ParseGenericParameterList();
        (SyntaxToken? colon, ImmutableArray<TypeSyntax> bases, ImmutableArray<SyntaxToken> baseCommas) = ParseBaseTypeList();
        ImmutableArray<WhereClauseSyntax> whereClauses = ParseWhereClauses();
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var members = ImmutableArray.CreateBuilder<TypeMemberDeclarationSyntax>();

        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            if (Current.Kind == SyntaxKind.ConstKeyword)
            {
                members.Add(ParseStructConstantDeclaration());
                continue;
            }
            (SyntaxToken? accessModifier, SyntaxToken? @static, SyntaxToken? threadlocal, SyntaxToken? @virtual, SyntaxToken? @override, SyntaxToken? @abstract, SyntaxToken? @readonly) = ParseStructMemberModifiers();
            SyntaxToken?[] modifiers = [accessModifier, @static, threadlocal, @virtual, @override, @abstract, @readonly];

            if (Current.Kind == SyntaxKind.TildeToken)
            {
                ValidateMemberModifiers("destructor", modifiers, SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.VirtualKeyword, SyntaxKind.OverrideKeyword);
                members.Add(ParseDestructorDeclaration(accessModifier, @virtual, @override));
            }
            else if (Current.Kind == SyntaxKind.IdentifierToken &&
                     string.Equals(Current.Text, identifier.Text, StringComparison.Ordinal) &&
                     Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
            {
                ValidateMemberModifiers("constructor", modifiers, SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword);
                members.Add(ParseConstructorDeclaration(accessModifier));
            }
            else
            {
                TypeSyntax type = ParseType();
                SyntaxToken? methodReadonly = ParseMethodReadonlyKeyword();
                if (Current.Kind == SyntaxKind.ThisKeyword)
                {
                    ValidateMemberModifiers("indexer", modifiers, SyntaxKind.PublicKeyword,
                        SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword, SyntaxKind.VirtualKeyword,
                        SyntaxKind.OverrideKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.ReadonlyKeyword);
                    ValidateAccessorReturnBinding(type);
                    members.Add(ParseIndexerDeclaration(accessModifier, @static, @virtual, @override, @abstract, @readonly, type));
                    continue;
                }
                SyntaxToken memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    ValidateMemberModifiers("method", modifiers, SyntaxKind.PublicKeyword,
                        SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword, SyntaxKind.VirtualKeyword,
                        SyntaxKind.OverrideKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.ReadonlyKeyword);
                    (type, methodReadonly) = FinishMethodReturnType(type, @readonly, methodReadonly);
                    members.Add(ParseMethodDeclaration(accessModifier, @static, @virtual, @override, @abstract, methodReadonly, type, memberIdentifier));
                }
                else if (Current.Kind == SyntaxKind.OpenBraceToken)
                {
                    ValidateMemberModifiers("property", modifiers, SyntaxKind.PublicKeyword,
                        SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword, SyntaxKind.VirtualKeyword,
                        SyntaxKind.OverrideKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.ReadonlyKeyword);
                    ValidateAccessorReturnBinding(type);
                    members.Add(ParsePropertyDeclaration(accessModifier, @static, @virtual, @override, @abstract, @readonly, type, memberIdentifier));
                }
                else
                {
                    ValidateMemberModifiers("field", modifiers, SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword, SyntaxKind.ThreadLocalKeyword, SyntaxKind.ReadonlyKeyword);
                    if (threadlocal is not null && @static is null)
                        Diagnostics.Report(threadlocal.Location, "threadlocal fields must be static",
                            DiagnosticIds.InvalidThreadLocalPlacement);
                    if (@readonly is not null && type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null && !type.Contains<PointerTypeSyntax>() && !type.Contains<ReferenceTypeSyntax>())
                        Diagnostics.Report(type.GetQualifier(SyntaxKind.ReadonlyKeyword)!.Location, "duplicate readonly field modifier",
                            DiagnosticIds.DuplicateModifier);
                    // On pointer fields a leading readonly qualifies the pointee.
                    if (@readonly is not null && (type.Contains<PointerTypeSyntax>() || type.Contains<ReferenceTypeSyntax>()) && type.GetQualifier(SyntaxKind.ReadonlyKeyword) is null)
                    {
                        type = new QualifiedTypeSyntax(type, @readonly);
                        @readonly = null;
                    }
                    SyntaxToken? equals = Current.Kind == SyntaxKind.EqualsToken ? NextToken() : null;
                    ExpressionSyntax? initializer = equals is null ? null : ParseExpression();
                    SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                    members.Add(new FieldDeclarationSyntax(accessModifier, @static, threadlocal, @readonly, type, memberIdentifier, equals, initializer, semicolon));
                }
            }

            if (_position == start)
            {
                NextToken();
            }
        }

        SyntaxToken closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
        return new StructDeclarationSyntax(
            structKeyword,
            identifier,
            typeParameters,
            colon,
            bases,
            baseCommas,
            whereClauses,
            openBrace,
            members.ToImmutable(),
            closeBrace) { AbstractKeyword = abstractKeyword };
    }

    private TemplateDeclarationSyntax ParseTemplateDeclaration()
    {
        SyntaxToken templateKeyword = MatchToken(SyntaxKind.TemplateKeyword);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var members = ImmutableArray.CreateBuilder<TypeMemberDeclarationSyntax>();

        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            var (access, @static, threadlocal, @virtual, @override, @abstract, @readonly) = ParseStructMemberModifiers();
            SyntaxToken?[] modifiers = [access, @static, threadlocal, @virtual, @override, @abstract, @readonly];

            if (Current.Kind == SyntaxKind.IdentifierToken && Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
            {
                ValidateMemberModifiers("template constructor", modifiers,
                    SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword);
                members.Add(ParseTemplateConstructorDeclaration(access, identifier));
            }
            else
            {
                ValidateMemberModifiers("template member", modifiers,
                    SyntaxKind.PublicKeyword, SyntaxKind.PrivateKeyword, SyntaxKind.StaticKeyword,
                    SyntaxKind.ReadonlyKeyword);
                TypeSyntax type = ParseType();
                SyntaxToken? methodReadonly = ParseMethodReadonlyKeyword();
                if (Current.Kind == SyntaxKind.ThisKeyword)
                {
                    ValidateAccessorReturnBinding(type);
                    IndexerDeclarationSyntax indexer = ParseIndexerDeclaration(
                        access, @static, null, null, null, @readonly, type);
                    ReportTemplateAccessorBodies(indexer.Accessors);
                    members.Add(indexer);
                    continue;
                }

                SyntaxToken memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    (type, methodReadonly) = FinishMethodReturnType(type, @readonly, methodReadonly);
                    members.Add(ParseTemplateMethodDeclaration(access, @static, methodReadonly, type, memberIdentifier));
                }
                else if (Current.Kind == SyntaxKind.OpenBraceToken)
                {
                    ValidateAccessorReturnBinding(type);
                    PropertyDeclarationSyntax property = ParsePropertyDeclaration(
                        access, @static, null, null, null, @readonly, type, memberIdentifier);
                    ReportTemplateAccessorBodies(property.Accessors);
                    members.Add(property);
                }
                else
                {
                    Diagnostics.Report(memberIdentifier.Location,
                        "structural templates may contain only method, property, indexer and constructor requirements",
                        DiagnosticIds.InvalidTemplateMember);
                    SyntaxToken? equals = Current.Kind == SyntaxKind.EqualsToken ? NextToken() : null;
                    ExpressionSyntax? initializer = equals is null ? null : ParseExpression();
                    members.Add(new FieldDeclarationSyntax(
                        access, @static, threadlocal, @readonly, type, memberIdentifier, equals, initializer,
                        MatchToken(SyntaxKind.SemicolonToken)));
                }
            }

            if (_position == start) NextToken();
        }

        return new TemplateDeclarationSyntax(
            templateKeyword,
            identifier,
            openBrace,
            members.ToImmutable(),
            MatchToken(SyntaxKind.CloseBraceToken));
    }

    private TemplateConstructorDeclarationSyntax ParseTemplateConstructorDeclaration(
        SyntaxToken? accessModifier,
        SyntaxToken templateIdentifier)
    {
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        if (!string.Equals(identifier.Text, templateIdentifier.Text, StringComparison.Ordinal))
            Diagnostics.Report(identifier.Location,
                $"template constructor must be named '{templateIdentifier.Text}'",
                DiagnosticIds.InvalidTemplateConstructorName);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commas) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        BlockStatementSyntax? body = null;
        SyntaxToken? semicolon = null;
        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            Diagnostics.Report(Current.Location, "template constructor requirements cannot have a body",
                DiagnosticIds.TemplateMemberBodyNotAllowed);
            body = ParseBlockStatement();
        }
        else
        {
            semicolon = MatchToken(SyntaxKind.SemicolonToken);
        }

        return new TemplateConstructorDeclarationSyntax(
            accessModifier, identifier, openParenthesis, parameters, commas, closeParenthesis, body, semicolon);
    }

    private MethodDeclarationSyntax ParseTemplateMethodDeclaration(
        SyntaxToken? accessModifier,
        SyntaxToken? @static,
        SyntaxToken? @readonly,
        TypeSyntax returnType,
        SyntaxToken identifier)
    {
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commas) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        BlockStatementSyntax? body = null;
        SyntaxToken? semicolon = null;
        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            Diagnostics.Report(Current.Location, "template method requirements cannot have a body",
                DiagnosticIds.TemplateMemberBodyNotAllowed);
            body = ParseBlockStatement();
        }
        else
        {
            semicolon = MatchToken(SyntaxKind.SemicolonToken);
        }

        return new MethodDeclarationSyntax(
            accessModifier, @static, null, null, null, @readonly, returnType, identifier,
            openParenthesis, parameters, commas, closeParenthesis, body, semicolon);
    }

    private void ReportTemplateAccessorBodies(IEnumerable<PropertyAccessorDeclarationSyntax> accessors)
    {
        foreach (PropertyAccessorDeclarationSyntax accessor in accessors.Where(accessor => accessor.Body is not null))
            Diagnostics.Report(accessor.Body!.OpenBraceToken.Location,
                "template property and indexer requirements cannot have accessor bodies",
                DiagnosticIds.TemplateMemberBodyNotAllowed);
    }

    private ModuleConstantDeclarationSyntax ParseModuleConstantDeclaration()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.ConstKeyword);
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken equals = MatchToken(SyntaxKind.EqualsToken);
        ExpressionSyntax initializer = ParseExpression();
        return new ModuleConstantDeclarationSyntax(keyword, type, identifier, equals, initializer, MatchToken(SyntaxKind.SemicolonToken));
    }

    private TypeConstantDeclarationSyntax ParseStructConstantDeclaration()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.ConstKeyword);
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken equals = MatchToken(SyntaxKind.EqualsToken);
        ExpressionSyntax initializer = ParseExpression();
        return new TypeConstantDeclarationSyntax(keyword, type, identifier, equals, initializer, MatchToken(SyntaxKind.SemicolonToken));
    }

    private MethodDeclarationSyntax ParseMethodDeclaration(
        SyntaxToken? accessModifier,
        SyntaxToken? @static,
        SyntaxToken? @virtual,
        SyntaxToken? @override,
        SyntaxToken? @abstract,
        SyntaxToken? @readonly,
        TypeSyntax returnType,
        SyntaxToken identifier)
    {
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commas) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        BlockStatementSyntax? body = @abstract is null ? ParseBlockStatement() : null;
        SyntaxToken? semicolon = @abstract is null ? null : MatchToken(SyntaxKind.SemicolonToken);
        return new MethodDeclarationSyntax(
            accessModifier,
            @static,
            @virtual,
            @override,
            @abstract,
            @readonly,
            returnType,
            identifier,
            openParenthesis,
            parameters,
            commas,
            closeParenthesis,
            body,
            semicolon);
    }

    private PropertyDeclarationSyntax ParsePropertyDeclaration(
        SyntaxToken? accessModifier,
        SyntaxToken? @static,
        SyntaxToken? @virtual,
        SyntaxToken? @override,
        SyntaxToken? @abstract,
        SyntaxToken? @readonly,
        TypeSyntax type,
        SyntaxToken identifier)
    {
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var accessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            SyntaxToken keyword;
            if (Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword)
            {
                keyword = NextToken();
            }
            else
            {
                Diagnostics.Report(Current.Location, "expected 'get' or 'set' property accessor",
                    DiagnosticIds.ExpectedAccessor);
                NextToken();
                continue;
            }

            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, null, NextToken()));
            }
            else
            {
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, ParseBlockStatement(), null));
            }
        }

        ValidateReadonlyAccessor(@readonly, accessors);
        return new PropertyDeclarationSyntax(
            accessModifier,
            @static,
            @virtual,
            @override,
            @abstract,
            @readonly,
            type,
            identifier,
            openBrace,
            accessors.ToImmutable(),
            MatchToken(SyntaxKind.CloseBraceToken));
    }

    private IndexerDeclarationSyntax ParseIndexerDeclaration(
        SyntaxToken? accessModifier,
        SyntaxToken? @static,
        SyntaxToken? @virtual,
        SyntaxToken? @override,
        SyntaxToken? @abstract,
        SyntaxToken? @readonly,
        TypeSyntax type)
    {
        SyntaxToken thisKeyword = MatchToken(SyntaxKind.ThisKeyword);
        SyntaxToken openBracket = MatchToken(SyntaxKind.OpenBracketToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commas) =
            ParseParameterList(SyntaxKind.CloseBracketToken);
        SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var accessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            if (!ValidateAccessorKeyword()) continue;
            SyntaxToken keyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                ? NextToken()
                : MatchToken(SyntaxKind.GetKeyword);
            if (Current.Kind == SyntaxKind.SemicolonToken)
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, null, NextToken()));
            else
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, ParseBlockStatement(), null));
        }
        ValidateReadonlyAccessor(@readonly, accessors);
        return new IndexerDeclarationSyntax(
            accessModifier,
            @static,
            @virtual,
            @override,
            @abstract,
            @readonly,
            type,
            thisKeyword,
            openBracket,
            parameters,
            commas,
            closeBracket,
            openBrace,
            accessors.ToImmutable(),
            MatchToken(SyntaxKind.CloseBraceToken));
    }

    private ConstructorDeclarationSyntax ParseConstructorDeclaration(SyntaxToken? accessModifier)
    {
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commas) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        SyntaxToken? colon = null;
        SyntaxToken? baseKeyword = null;
        SyntaxToken? baseOpen = null;
        ImmutableArray<ExpressionSyntax> baseArguments = [];
        ImmutableArray<SyntaxToken> baseCommas = [];
        SyntaxToken? baseClose = null;
        if (Current.Kind == SyntaxKind.ColonToken)
        {
            colon = NextToken();
            baseKeyword = Current.Kind is SyntaxKind.BaseKeyword or SyntaxKind.ThisKeyword
                ? NextToken()
                : MatchToken(SyntaxKind.BaseKeyword);
            baseOpen = MatchToken(SyntaxKind.OpenParenthesisToken);
            (baseArguments, baseCommas) = ParseExpressionList(SyntaxKind.CloseParenthesisToken);
            baseClose = MatchToken(SyntaxKind.CloseParenthesisToken);
        }
        BlockStatementSyntax body = ParseBlockStatement();
        return new ConstructorDeclarationSyntax(
            accessModifier,
            identifier,
            openParenthesis,
            parameters,
            commas,
            closeParenthesis,
            colon,
            baseKeyword,
            baseOpen,
            baseArguments,
            baseCommas,
            baseClose,
            body);
    }

    private (SyntaxToken? Colon, ImmutableArray<TypeSyntax> Bases, ImmutableArray<SyntaxToken> Commas) ParseBaseTypeList()
    {
        if (Current.Kind != SyntaxKind.ColonToken)
            return (null, [], []);

        SyntaxToken colon = NextToken();
        var bases = ImmutableArray.CreateBuilder<TypeSyntax>();
        var commas = ImmutableArray.CreateBuilder<SyntaxToken>();
        do
        {
            bases.Add(ParseType(allowArraySuffix: false));
            if (Current.Kind != SyntaxKind.CommaToken)
                break;
            commas.Add(NextToken());
        } while (true);
        return (colon, bases.ToImmutable(), commas.ToImmutable());
    }

    private GenericParameterListSyntax? ParseGenericParameterList()
    {
        if (Current.Kind != SyntaxKind.LessToken) return null;

        SyntaxToken less = NextToken();
        var parameters = ImmutableArray.CreateBuilder<GenericParameterSyntax>();
        var commas = ImmutableArray.CreateBuilder<SyntaxToken>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            SyntaxToken name = MatchToken(SyntaxKind.IdentifierToken);
            parameters.Add(new GenericParameterSyntax(name));
            if (!name.IsMissing && !names.Add(name.Text))
                Diagnostics.Report(name.Location, $"generic parameter '{name.Text}' is already declared",
                    DiagnosticIds.DuplicateGenericParameter);
            if (Current.Kind != SyntaxKind.CommaToken) break;
            commas.Add(NextToken());
        } while (Current.Kind != SyntaxKind.EndOfFileToken);

        return new GenericParameterListSyntax(
            less, parameters.ToImmutable(), commas.ToImmutable(), MatchTypeGreaterToken());
    }

    private ImmutableArray<WhereClauseSyntax> ParseWhereClauses()
    {
        var clauses = ImmutableArray.CreateBuilder<WhereClauseSyntax>();
        while (Current.Kind == SyntaxKind.WhereKeyword)
        {
            SyntaxToken whereKeyword = NextToken();
            SyntaxToken typeParameter = MatchToken(SyntaxKind.IdentifierToken);
            SyntaxToken colon = MatchToken(SyntaxKind.ColonToken);
            var constraints = ImmutableArray.CreateBuilder<GenericConstraintSyntax>();
            var commas = ImmutableArray.CreateBuilder<SyntaxToken>();
            do
            {
                constraints.Add(new GenericConstraintSyntax(ParseType(allowArraySuffix: false)));
                if (Current.Kind != SyntaxKind.CommaToken) break;
                commas.Add(NextToken());
            } while (Current.Kind != SyntaxKind.EndOfFileToken);
            clauses.Add(new WhereClauseSyntax(
                whereKeyword, typeParameter, colon, constraints.ToImmutable(), commas.ToImmutable()));
        }
        return clauses.ToImmutable();
    }

    private InterfaceDeclarationSyntax ParseInterfaceDeclaration()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.InterfaceKeyword);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        (SyntaxToken? colon, ImmutableArray<TypeSyntax> bases, ImmutableArray<SyntaxToken> commas) = ParseBaseTypeList();
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var methods = ImmutableArray.CreateBuilder<InterfaceMethodDeclarationSyntax>();
        var properties = ImmutableArray.CreateBuilder<InterfacePropertyDeclarationSyntax>();
        var indexers = ImmutableArray.CreateBuilder<InterfaceIndexerDeclarationSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            var (access, @static, threadlocal, @virtual, @override, @abstract, readonlyKeyword) = ParseStructMemberModifiers();
            ValidateMemberModifiers("interface member", [access, @static, threadlocal, @virtual, @override, @abstract, readonlyKeyword], SyntaxKind.ReadonlyKeyword);
            TypeSyntax returnType = ParseType();
            SyntaxToken? methodReadonly = ParseMethodReadonlyKeyword();
            if (Current.Kind == SyntaxKind.ThisKeyword)
            {
                ValidateAccessorReturnBinding(returnType);
                SyntaxToken thisKeyword = NextToken();
                SyntaxToken indexOpen = MatchToken(SyntaxKind.OpenBracketToken);
                (ImmutableArray<ParameterSyntax> indexParameters, ImmutableArray<SyntaxToken> indexCommas) =
                    ParseParameterList(SyntaxKind.CloseBracketToken);
                SyntaxToken indexClose = MatchToken(SyntaxKind.CloseBracketToken);
                SyntaxToken accessorOpen = MatchToken(SyntaxKind.OpenBraceToken);
                var indexAccessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
                while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
                {
                    if (!ValidateAccessorKeyword()) continue;
                    SyntaxToken accessorKeyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                        ? NextToken()
                        : MatchToken(SyntaxKind.GetKeyword);
                    indexAccessors.Add(new PropertyAccessorDeclarationSyntax(
                        accessorKeyword,
                        null,
                        MatchToken(SyntaxKind.SemicolonToken)));
                }
                ValidateReadonlyAccessor(readonlyKeyword, indexAccessors);
                indexers.Add(new InterfaceIndexerDeclarationSyntax(
                    readonlyKeyword,
                    returnType,
                    thisKeyword,
                    indexOpen,
                    indexParameters,
                    indexCommas,
                    indexClose,
                    accessorOpen,
                    indexAccessors.ToImmutable(),
                    MatchToken(SyntaxKind.CloseBraceToken)));
                continue;
            }
            SyntaxToken name = MatchToken(SyntaxKind.IdentifierToken);
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                (returnType, methodReadonly) = FinishMethodReturnType(returnType, readonlyKeyword, methodReadonly);
                SyntaxToken open = MatchToken(SyntaxKind.OpenParenthesisToken);
                (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> methodCommas) = ParseParameterList();
                SyntaxToken close = MatchToken(SyntaxKind.CloseParenthesisToken);
                SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                methods.Add(new InterfaceMethodDeclarationSyntax(methodReadonly, returnType, name, open, parameters, methodCommas, close, semicolon));
            }
            else
            {
                ValidateAccessorReturnBinding(returnType);
                SyntaxToken propertyOpen = MatchToken(SyntaxKind.OpenBraceToken);
                var accessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
                while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
                {
                    if (!ValidateAccessorKeyword()) continue;
                    SyntaxToken accessorKeyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                        ? NextToken()
                        : MatchToken(SyntaxKind.GetKeyword);
                    SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                    accessors.Add(new PropertyAccessorDeclarationSyntax(accessorKeyword, null, semicolon));
                }
                ValidateReadonlyAccessor(readonlyKeyword, accessors);
                properties.Add(new InterfacePropertyDeclarationSyntax(
                    readonlyKeyword,
                    returnType,
                    name,
                    propertyOpen,
                    accessors.ToImmutable(),
                    MatchToken(SyntaxKind.CloseBraceToken)));
            }
        }
        return new InterfaceDeclarationSyntax(keyword, identifier, colon, bases, commas, openBrace, methods.ToImmutable(), properties.ToImmutable(), indexers.ToImmutable(), MatchToken(SyntaxKind.CloseBraceToken));
    }

    private (SyntaxToken? Access, SyntaxToken? Static, SyntaxToken? ThreadLocal, SyntaxToken? Virtual, SyntaxToken? Override, SyntaxToken? Abstract, SyntaxToken? Readonly) ParseStructMemberModifiers()
    {
        SyntaxToken? access = null, @static = null, threadlocal = null, @virtual = null, @override = null, @abstract = null, @readonly = null;
        while (Current.Kind is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.StaticKeyword or SyntaxKind.ThreadLocalKeyword or SyntaxKind.VirtualKeyword or SyntaxKind.OverrideKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.ReadonlyKeyword)
        {
            if (Current.Kind == SyntaxKind.ReadonlyKeyword && @readonly is not null)
                break;
            SyntaxToken modifier = NextToken();
            SyntaxToken? previous = modifier.Kind switch
            {
                SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword => access,
                SyntaxKind.StaticKeyword => @static,
                SyntaxKind.ThreadLocalKeyword => threadlocal,
                SyntaxKind.VirtualKeyword => @virtual,
                SyntaxKind.OverrideKeyword => @override,
                SyntaxKind.AbstractKeyword => @abstract,
                _ => @readonly,
            };
            if (previous is not null)
                Diagnostics.Report(modifier.Location, $"duplicate or conflicting modifier '{modifier.Text}'",
                    DiagnosticIds.DuplicateModifier);
            switch (modifier.Kind)
            {
                case SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword: access ??= modifier; break;
                case SyntaxKind.StaticKeyword: @static ??= modifier; break;
                case SyntaxKind.ThreadLocalKeyword: threadlocal ??= modifier; break;
                case SyntaxKind.VirtualKeyword: @virtual ??= modifier; break;
                case SyntaxKind.OverrideKeyword: @override ??= modifier; break;
                case SyntaxKind.AbstractKeyword: @abstract ??= modifier; break;
                case SyntaxKind.ReadonlyKeyword: @readonly ??= modifier; break;
            }
        }
        SyntaxToken[] dispatch = new[] { @virtual, @override, @abstract }.OfType<SyntaxToken>().ToArray();
        foreach (SyntaxToken conflicting in dispatch.Skip(1))
            Diagnostics.Report(conflicting.Location, "virtual, override and abstract modifiers are mutually exclusive",
                DiagnosticIds.ConflictingDispatchModifiers);
        if (@static is not null && dispatch.Length != 0)
            Diagnostics.Report(dispatch[0].Location, "static members cannot be virtual, override or abstract",
                DiagnosticIds.StaticDispatchModifierNotAllowed);
        return (access, @static, threadlocal, @virtual, @override, @abstract, @readonly);
    }

    private void ValidateMemberModifiers(string declaration, SyntaxToken?[] modifiers, params SyntaxKind[] allowed)
    {
        foreach (SyntaxToken modifier in modifiers.OfType<SyntaxToken>())
            if (!allowed.Contains(modifier.Kind))
                Diagnostics.Report(modifier.Location, $"modifier '{modifier.Text}' is not allowed on a {declaration}",
                    modifier.Kind == SyntaxKind.ThreadLocalKeyword
                        ? DiagnosticIds.InvalidThreadLocalPlacement
                        : DiagnosticIds.ModifierNotAllowed);
    }

    private void ValidateAccessorReturnBinding(TypeSyntax type)
    {
        if (type.GetQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix) is { } modifier)
            Diagnostics.Report(modifier.Location, "return types cannot have a readonly pointer binding",
                DiagnosticIds.ReadonlyReturnBindingNotAllowed);
    }

    private void ValidateReadonlyAccessor(SyntaxToken? modifier, IEnumerable<PropertyAccessorDeclarationSyntax> accessors)
    {
        if (modifier is not null && !accessors.Any(accessor => accessor.IsGetter))
            Diagnostics.Report(modifier.Location, "readonly accessor modifier requires a getter",
                DiagnosticIds.ReadonlyAccessorRequiresGetter);
    }

    private bool ValidateAccessorKeyword()
    {
        if (Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword) return true;
        Diagnostics.Report(Current.Location, "expected 'get' or 'set' accessor; accessor modifiers are not supported",
            DiagnosticIds.AccessorModifiersNotSupported);
        NextToken();
        return false;
    }

    private DestructorDeclarationSyntax ParseDestructorDeclaration(SyntaxToken? accessModifier, SyntaxToken? @virtual, SyntaxToken? @override)
    {
        SyntaxToken tilde = MatchToken(SyntaxKind.TildeToken);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        BlockStatementSyntax body = ParseBlockStatement();
        return new DestructorDeclarationSyntax(
            accessModifier,
            @virtual,
            tilde,
            identifier,
            openParenthesis,
            closeParenthesis,
            body) { OverrideKeyword = @override };
    }

    private NamespaceDeclarationSyntax ParseNamespaceDeclaration()
    {
        SyntaxToken namespaceKeyword = MatchToken(SyntaxKind.NamespaceKeyword);
        var nameParts = ImmutableArray.CreateBuilder<SyntaxToken>();
        var dotTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        while (Current.Kind == SyntaxKind.DotToken)
        {
            dotTokens.Add(NextToken());
            nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        }

        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new NamespaceDeclarationSyntax(
            namespaceKeyword,
            nameParts.ToImmutable(),
            dotTokens.ToImmutable(),
            semicolon);
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration()
    {
        SyntaxToken? accessModifier = ParseAccessModifier();
        SyntaxToken? abiModifier = Current.Kind is SyntaxKind.ExternKeyword or SyntaxKind.ExportKeyword
            ? NextToken()
            : null;

        // Also accept `export public` / `extern public` for convenience.
        accessModifier ??= ParseAccessModifier();

        TypeSyntax returnType = ParseType();
        SyntaxToken? methodReadonly = ParseMethodReadonlyKeyword();
        (returnType, methodReadonly) = FinishMethodReturnType(returnType, null, methodReadonly);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        GenericParameterListSyntax? typeParameters = ParseGenericParameterList();
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commaTokens) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        ImmutableArray<WhereClauseSyntax> whereClauses = ParseWhereClauses();
        BlockStatementSyntax? body = null;
        SyntaxToken? semicolon = null;

        if (abiModifier?.Kind == SyntaxKind.ExternKeyword)
        {
            semicolon = MatchToken(SyntaxKind.SemicolonToken);
        }
        else
        {
            body = ParseBlockStatement();
        }

        return new FunctionDeclarationSyntax(
            accessModifier,
            abiModifier,
            returnType,
            identifier,
            typeParameters,
            openParenthesis,
            parameters,
            commaTokens,
            closeParenthesis,
            whereClauses,
            body,
            semicolon) { ReadonlyKeyword = methodReadonly };
    }

    private SyntaxToken? ParseMethodReadonlyKeyword() =>
        Current.Kind == SyntaxKind.ReadonlyKeyword &&
        Peek(1).Kind == SyntaxKind.IdentifierToken &&
        Peek(2).Kind == SyntaxKind.OpenParenthesisToken
            ? NextToken()
            : null;

    private (TypeSyntax Type, SyntaxToken? Readonly) FinishMethodReturnType(
        TypeSyntax type, SyntaxToken? leadingReadonly, SyntaxToken? methodReadonly)
    {
        // A leading member qualifier belongs to the return type, never to this.
        if (leadingReadonly is not null)
        {
            if (type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null)
                Diagnostics.Report(type.GetQualifier(SyntaxKind.ReadonlyKeyword)!.Location, "duplicate readonly return type qualifier",
                    DiagnosticIds.DuplicateModifier);
            type = new QualifiedTypeSyntax(type, leadingReadonly);
        }

        if (type.GetQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix) is { } pointerReadonly)
        {
            // ParseType also serves variables. Only their '*' suffix can qualify
            // a binding; immediately before a method name it qualifies the method.
            if (type.Contains<ReferenceTypeSyntax>() || type.Contains<ArrayTypeSyntax>())
                Diagnostics.Report(pointerReadonly.Location, "return types cannot have a readonly pointer binding",
                    DiagnosticIds.ReadonlyReturnBindingNotAllowed);
            else if (methodReadonly is not null)
                Diagnostics.Report(methodReadonly.Location, "duplicate readonly method qualifier",
                    DiagnosticIds.DuplicateModifier);
            else
                methodReadonly = pointerReadonly;
            type = type.WithoutQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix);
        }

        return (type, methodReadonly);
    }

    private SyntaxToken? ParseAccessModifier() =>
        Current.Kind is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword ? NextToken() : null;

    private (ImmutableArray<ParameterSyntax> Parameters, ImmutableArray<SyntaxToken> Commas) ParseParameterList(
        SyntaxKind closeTokenKind = SyntaxKind.CloseParenthesisToken)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSyntax>();
        var commaTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (Current.Kind != closeTokenKind && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            parameters.Add(ParseParameter());
            if (Current.Kind != SyntaxKind.CommaToken)
            {
                break;
            }

            commaTokens.Add(NextToken());
        }

        return (parameters.ToImmutable(), commaTokens.ToImmutable());
    }

    private ParameterSyntax ParseParameter()
    {
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        return new ParameterSyntax(type, identifier);
    }

    private TypeSyntax ParseType(bool allowArraySuffix = true)
    {
        SyntaxToken? readonlyKeyword = Current.Kind == SyntaxKind.ReadonlyKeyword ? NextToken() : null;
        SyntaxToken? constKeyword = Current.Kind == SyntaxKind.ConstKeyword ? NextToken() : null;
        var nameParts = ImmutableArray.CreateBuilder<SyntaxToken>();
        var dotTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        SyntaxToken firstName = SyntaxFacts.IsTypeName(Current.Kind)
            ? NextToken() : MatchToken(SyntaxKind.IdentifierToken);
        nameParts.Add(firstName);
        if (firstName.Kind == SyntaxKind.IdentifierToken)
        {
            while (Current.Kind == SyntaxKind.DotToken)
            {
                dotTokens.Add(NextToken());
                nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
            }
        }

        TypeArgumentListSyntax? arguments = Current.Kind == SyntaxKind.LessToken ? ParseTypeArgumentList() : null;
        TypeSyntax type = new NamedTypeSyntax(nameParts.ToImmutable(), dotTokens.ToImmutable(), arguments);
        while (Current.Kind == SyntaxKind.StarToken)
            type = new PointerTypeSyntax(type, NextToken());
        if (type is PointerTypeSyntax && Current.Kind == SyntaxKind.ReadonlyKeyword)
            type = new QualifiedTypeSyntax(type, NextToken(), TypeQualifierPosition.Postfix);
        if (Current.Kind == SyntaxKind.AmpersandToken)
            type = new ReferenceTypeSyntax(type, NextToken());
        if (allowArraySuffix)
            type = ParseArrayTypeSuffixes(type, allocation: false);
        if (constKeyword is not null) type = new QualifiedTypeSyntax(type, constKeyword);
        if (readonlyKeyword is not null) type = new QualifiedTypeSyntax(type, readonlyKeyword);
        return type;
    }

    private TypeSyntax ParseArrayTypeSuffixes(TypeSyntax type, bool allocation)
    {
        var suffixes = new List<(SyntaxToken Open, ImmutableArray<SyntaxToken> Commas, SyntaxToken Close)>();
        while (Current.Kind == SyntaxKind.OpenBracketToken &&
               (!allocation || Peek(1).Kind is SyntaxKind.CommaToken or SyntaxKind.CloseBracketToken))
        {
            SyntaxToken open = NextToken();
            var commas = ImmutableArray.CreateBuilder<SyntaxToken>();
            while (Current.Kind == SyntaxKind.CommaToken) commas.Add(NextToken());
            if (Current.Kind != SyntaxKind.CloseBracketToken)
            {
                Diagnostics.Report(Current.Location,
                    "fixed-size array type syntax is not supported; use 'T[]' and initialize it with 'T[n]' or 'new T[n]'",
                    DiagnosticIds.FixedSizeArrayTypeNotSupported);
                while (Current.Kind is not (SyntaxKind.CloseBracketToken or SyntaxKind.EndOfFileToken or
                       SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken or SyntaxKind.CloseParenthesisToken))
                    NextToken();
            }
            suffixes.Add((open, commas.ToImmutable(), MatchToken(SyntaxKind.CloseBracketToken)));
        }
        for (int index = suffixes.Count - 1; index >= 0; index--)
        {
            var suffix = suffixes[index];
            type = new ArrayTypeSyntax(type, suffix.Open, suffix.Commas, suffix.Close);
        }
        return type;
    }

    // Reusable type grammar. Expression shifts retain their original lexer tokens.
    private TypeArgumentListSyntax ParseTypeArgumentList()
    {
        SyntaxToken less = MatchToken(SyntaxKind.LessToken);
        var arguments = ImmutableArray.CreateBuilder<TypeSyntax>();
        var commas = ImmutableArray.CreateBuilder<SyntaxToken>();
        do
        {
            int start = _position;
            arguments.Add(ParseType());
            if (Current.Kind != SyntaxKind.CommaToken) break;
            commas.Add(NextToken());
            if (_position == start) break;
        } while (Current.Kind != SyntaxKind.EndOfFileToken);
        return new TypeArgumentListSyntax(less, arguments.ToImmutable(), commas.ToImmutable(), MatchTypeGreaterToken());
    }

    private SyntaxToken MatchTypeGreaterToken()
    {
        if (Current.Kind is SyntaxKind.GreaterGreaterToken or SyntaxKind.GreaterGreaterEqualsToken or SyntaxKind.GreaterOrEqualsToken)
        {
            SyntaxToken token = Current;
            SyntaxKind remainder = token.Kind switch
            {
                SyntaxKind.GreaterGreaterToken => SyntaxKind.GreaterToken,
                SyntaxKind.GreaterGreaterEqualsToken => SyntaxKind.GreaterOrEqualsToken,
                _ => SyntaxKind.EqualsToken,
            };
            _tokens[_position] = new SyntaxToken(SyntaxKind.GreaterToken,
                new TextLocation(token.Location.Source, new TextSpan(token.Location.Span.Start, 1)), ">");
            _tokens.Insert(_position + 1, new SyntaxToken(remainder,
                new TextLocation(token.Location.Source, new TextSpan(token.Location.Span.Start + 1, token.Text.Length - 1)),
                token.Text[1..]));
        }
        return MatchToken(SyntaxKind.GreaterToken);
    }

    private BlockStatementSyntax ParseBlockStatement()
    {
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            statements.Add(ParseStatement());

            if (_position == start)
            {
                NextToken();
            }
        }

        SyntaxToken closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
        return new BlockStatementSyntax(openBrace, statements.ToImmutable(), closeBrace);
    }

    private StatementSyntax ParseStatement()
    {
        if (Current.Kind == SyntaxKind.ThreadLocalKeyword ||
            Current.Kind == SyntaxKind.StaticKeyword && Peek(1).Kind == SyntaxKind.ThreadLocalKeyword)
        {
            if (Current.Kind == SyntaxKind.StaticKeyword) NextToken();
            SyntaxToken threadlocal = NextToken();
            Diagnostics.Report(threadlocal.Location,
                "threadlocal is allowed only on static fields",
                DiagnosticIds.InvalidThreadLocalPlacement);
        }
        if (Current.Kind == SyntaxKind.SwitchKeyword) return ParseSwitchStatement();
        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            return ParseBlockStatement();
        }

        if (Current.Kind == SyntaxKind.ReturnKeyword)
        {
            return ParseReturnStatement();
        }

        if (Current.Kind == SyntaxKind.IfKeyword)
        {
            return ParseIfStatement();
        }

        if (Current.Kind == SyntaxKind.WhileKeyword)
        {
            return ParseWhileStatement();
        }

        if (Current.Kind == SyntaxKind.ForKeyword)
        {
            return ParseForStatement();
        }

        if (Current.Kind == SyntaxKind.BreakKeyword)
        {
            return ParseBreakStatement();
        }

        if (Current.Kind == SyntaxKind.ContinueKeyword)
        {
            return ParseContinueStatement();
        }

        if (IsVariableDeclaration())
        {
            return ParseVariableDeclarationStatement();
        }

        return ParseExpressionStatement();
    }

    private SwitchStatementSyntax ParseSwitchStatement()
    {
        SyntaxToken keyword = NextToken();
        MatchToken(SyntaxKind.OpenParenthesisToken);
        ExpressionSyntax expression = ParseExpression();
        MatchToken(SyntaxKind.CloseParenthesisToken);
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var sections = ImmutableArray.CreateBuilder<SwitchSectionSyntax>();
        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            SyntaxToken label = Current.Kind == SyntaxKind.DefaultKeyword ? NextToken() : MatchToken(SyntaxKind.CaseKeyword);
            ExpressionSyntax? value = label.Kind == SyntaxKind.DefaultKeyword ? null : ParseExpression(allowTrailingColon: true);
            MatchToken(SyntaxKind.ColonToken);
            var statements = ImmutableArray.CreateBuilder<StatementSyntax>();
            while (Current.Kind is not SyntaxKind.CaseKeyword and not SyntaxKind.DefaultKeyword and not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
            {
                int statementStart = _position;
                statements.Add(ParseStatement());
                if (_position == statementStart) NextToken();
            }
            sections.Add(new SwitchSectionSyntax(label, value, statements.ToImmutable()));
            if (_position == start) NextToken();
        }
        SyntaxToken closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
        return new SwitchStatementSyntax(keyword, expression, sections.ToImmutable())
        {
            OpenBraceToken = openBrace,
            CloseBraceToken = closeBrace,
        };
    }

    private ReturnStatementSyntax ParseReturnStatement()
    {
        SyntaxToken returnKeyword = MatchToken(SyntaxKind.ReturnKeyword);
        ExpressionSyntax? expression = Current.Kind == SyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ReturnStatementSyntax(returnKeyword, expression, semicolon);
    }

    private IfStatementSyntax ParseIfStatement()
    {
        SyntaxToken ifKeyword = MatchToken(SyntaxKind.IfKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        ExpressionSyntax condition = ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax thenStatement = ParseStatement();
        SyntaxToken? elseKeyword = null;
        StatementSyntax? elseStatement = null;

        if (Current.Kind == SyntaxKind.ElseKeyword)
        {
            elseKeyword = NextToken();
            elseStatement = ParseStatement();
        }

        return new IfStatementSyntax(
            ifKeyword,
            openParenthesis,
            condition,
            closeParenthesis,
            thenStatement,
            elseKeyword,
            elseStatement);
    }

    private WhileStatementSyntax ParseWhileStatement()
    {
        SyntaxToken whileKeyword = MatchToken(SyntaxKind.WhileKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        ExpressionSyntax condition = ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax body = ParseStatement();
        return new WhileStatementSyntax(whileKeyword, openParenthesis, condition, closeParenthesis, body);
    }

    private ForStatementSyntax ParseForStatement()
    {
        SyntaxToken forKeyword = MatchToken(SyntaxKind.ForKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        StatementSyntax? initializer = null;
        SyntaxToken firstSemicolon;

        if (Current.Kind == SyntaxKind.SemicolonToken)
        {
            firstSemicolon = NextToken();
        }
        else if (IsVariableDeclaration())
        {
            var declaration = ParseVariableDeclarationStatement();
            initializer = declaration;
            firstSemicolon = declaration.SemicolonToken;
        }
        else
        {
            ExpressionSyntax expression = ParseExpression();
            firstSemicolon = MatchToken(SyntaxKind.SemicolonToken);
            initializer = new ExpressionStatementSyntax(expression, firstSemicolon);
        }

        ExpressionSyntax? condition = Current.Kind == SyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        SyntaxToken secondSemicolon = MatchToken(SyntaxKind.SemicolonToken);
        ExpressionSyntax? increment = Current.Kind == SyntaxKind.CloseParenthesisToken
            ? null
            : ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax body = ParseStatement();

        return new ForStatementSyntax(
            forKeyword,
            openParenthesis,
            initializer,
            firstSemicolon,
            condition,
            secondSemicolon,
            increment,
            closeParenthesis,
            body);
    }

    private BreakStatementSyntax ParseBreakStatement()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.BreakKeyword);
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new BreakStatementSyntax(keyword, semicolon);
    }

    private ContinueStatementSyntax ParseContinueStatement()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.ContinueKeyword);
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ContinueStatementSyntax(keyword, semicolon);
    }

    private VariableDeclarationStatementSyntax ParseVariableDeclarationStatement()
    {
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken? equals = null;
        ExpressionSyntax? initializer = null;

        if (Current.Kind == SyntaxKind.EqualsToken)
        {
            equals = NextToken();
            initializer = ParseExpression();
        }

        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new VariableDeclarationStatementSyntax(type, identifier, equals, initializer, semicolon);
    }

    private ExpressionStatementSyntax ParseExpressionStatement()
    {
        ExpressionSyntax expression = ParseExpression();
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ExpressionStatementSyntax(expression, semicolon);
    }

    private ExpressionSyntax ParseExpression(bool allowTrailingColon = false) =>
        ParseSwapExpression(allowTrailingColon);

    private ExpressionSyntax ParseSwapExpression(bool allowTrailingColon)
    {
        ExpressionSyntax left = ParseCompareExchangeExpression(allowTrailingColon);
        if (Current.Kind != SyntaxKind.SwapToken) return left;
        SyntaxToken operatorToken = NextToken();
        ExpressionSyntax right = ParseCompareExchangeExpression(allowTrailingColon);
        return new SwapExpressionSyntax(left, operatorToken, right);
    }

    private ExpressionSyntax ParseCompareExchangeExpression(bool allowTrailingColon)
    {
        ExpressionSyntax target = ParseAssignmentExpression();
        if (Current.Kind != SyntaxKind.ColonToken ||
            allowTrailingColon && !HasCompareExchangeArrowAhead())
            return target;

        SyntaxToken colon = NextToken();
        ExpressionSyntax expected = ParseAssignmentExpression();
        SyntaxToken arrow;
        if (Current.Kind == SyntaxKind.CompareExchangeArrowToken)
        {
            arrow = NextToken();
        }
        else if (Current.Kind == SyntaxKind.ArrowToken)
        {
            Diagnostics.Report(Current.Location,
                "compare-exchange uses '-->' after the expected value",
                DiagnosticIds.MalformedCompareExchange);
            arrow = NextToken();
        }
        else
        {
            Diagnostics.Report(Current.Location,
                "compare-exchange requires '-->' followed by the desired value",
                DiagnosticIds.MalformedCompareExchange);
            arrow = MatchToken(SyntaxKind.CompareExchangeArrowToken);
        }

        ExpressionSyntax desired = ParseAssignmentExpression();
        if (Current.Kind == SyntaxKind.ColonToken && HasCompareExchangeArrowAhead())
        {
            Diagnostics.Report(Current.Location,
                "compare-exchange expressions cannot be chained; parenthesize a separate expression",
                DiagnosticIds.ChainedCompareExchange);
            SkipChainedCompareExchangeTail();
        }

        return new CompareExchangeExpressionSyntax(target, colon, expected, arrow, desired);
    }

    private bool HasCompareExchangeArrowAhead()
    {
        int parenthesisDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (int offset = 1; ; offset++)
        {
            SyntaxKind kind = Peek(offset).Kind;
            switch (kind)
            {
                case SyntaxKind.OpenParenthesisToken: parenthesisDepth++; continue;
                case SyntaxKind.CloseParenthesisToken when parenthesisDepth > 0: parenthesisDepth--; continue;
                case SyntaxKind.OpenBracketToken: bracketDepth++; continue;
                case SyntaxKind.CloseBracketToken when bracketDepth > 0: bracketDepth--; continue;
                case SyntaxKind.OpenBraceToken: braceDepth++; continue;
                case SyntaxKind.CloseBraceToken when braceDepth > 0: braceDepth--; continue;
            }

            if (parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0) continue;
            if (kind is SyntaxKind.CompareExchangeArrowToken or SyntaxKind.ArrowToken) return true;
            if (kind is SyntaxKind.ColonToken or SyntaxKind.SemicolonToken or SyntaxKind.CommaToken or
                SyntaxKind.CloseParenthesisToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken or
                SyntaxKind.CaseKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.EndOfFileToken)
                return false;
        }
    }

    private void SkipChainedCompareExchangeTail()
    {
        NextToken();
        ParseAssignmentExpression();
        if (Current.Kind is SyntaxKind.CompareExchangeArrowToken or SyntaxKind.ArrowToken)
            NextToken();
        ParseAssignmentExpression();
    }

    private ExpressionSyntax ParseAssignmentExpression()
    {
        ExpressionSyntax left = ParseBinaryExpression();

        if (!SyntaxFacts.IsAssignmentOperator(Current.Kind))
        {
            return left;
        }

        SyntaxToken operatorToken = NextToken();
        ExpressionSyntax right = ParseAssignmentExpression();
        return new AssignmentExpressionSyntax(left, operatorToken, right);
    }

    private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        int unaryPrecedence = SyntaxFacts.GetUnaryOperatorPrecedence(Current.Kind);

        if (Current.Kind == SyntaxKind.MoveKeyword && 12 >= parentPrecedence)
        {
            SyntaxToken moveKeyword = NextToken();
            ExpressionSyntax operand = ParseBinaryExpression(12);
            left = new MoveExpressionSyntax(moveKeyword, operand);
        }
        else if (Current.Kind == SyntaxKind.LockKeyword && 12 >= parentPrecedence)
        {
            SyntaxToken lockKeyword = NextToken();
            ExpressionSyntax operand = ParseBinaryExpression(12);
            left = new LockExpressionSyntax(lockKeyword, operand);
        }
        else if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            SyntaxToken operatorToken = NextToken();
            ExpressionSyntax operand = ParseBinaryExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(operatorToken, operand);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            int precedence = SyntaxFacts.GetBinaryOperatorPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
            {
                break;
            }

            SyntaxToken operatorToken = NextToken();
            ExpressionSyntax right = ParseBinaryExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorToken, right);
        }

        return left;
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        ExpressionSyntax expression = ParsePrimaryExpression();

        while (true)
        {
            if (Current.Kind == SyntaxKind.OpenParenthesisToken)
            {
                expression = ParseCallExpression(expression, null);
                continue;
            }

            if (Current.Kind == SyntaxKind.LessToken && IsTypeArgumentListFollowedByCall())
            {
                TypeArgumentListSyntax typeArguments = ParseTypeArgumentList();
                expression = ParseCallExpression(expression, typeArguments);
                continue;
            }

            if (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                SyntaxToken openBracket = NextToken();
                (ImmutableArray<ExpressionSyntax> arguments, ImmutableArray<SyntaxToken> commas) =
                    ParseExpressionList(SyntaxKind.CloseBracketToken);
                SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
                expression = new IndexExpressionSyntax(expression, openBracket, arguments, commas, closeBracket);
                continue;
            }

            if (Current.Kind is SyntaxKind.DotToken or SyntaxKind.ArrowToken)
            {
                SyntaxToken operatorToken = NextToken();
                SyntaxToken memberToken = MatchToken(SyntaxKind.IdentifierToken);
                expression = new MemberAccessExpressionSyntax(expression, operatorToken, memberToken);
                continue;
            }

            break;
        }

        if (Current.Kind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
        {
            expression = new PostfixUnaryExpressionSyntax(expression, NextToken());
        }

        return expression;
    }

    private CallExpressionSyntax ParseCallExpression(ExpressionSyntax target, TypeArgumentListSyntax? typeArguments)
    {
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ExpressionSyntax> arguments, ImmutableArray<SyntaxToken> commas) =
            ParseExpressionList(SyntaxKind.CloseParenthesisToken);
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        return new CallExpressionSyntax(
            target,
            typeArguments,
            openParenthesis,
            arguments,
            commas,
            closeParenthesis);
    }

    private bool IsTypeArgumentListFollowedByCall()
    {
        int depth = 0;
        for (int offset = 0; ; offset++)
        {
            SyntaxKind kind = Peek(offset).Kind;
            if (kind == SyntaxKind.EndOfFileToken) return false;
            if (kind == SyntaxKind.LessToken)
            {
                depth++;
                continue;
            }
            if (kind == SyntaxKind.GreaterToken)
            {
                depth--;
            }
            else if (kind == SyntaxKind.GreaterGreaterToken)
            {
                depth -= 2;
            }
            else
            {
                continue;
            }

            if (depth == 0) return Peek(offset + 1).Kind == SyntaxKind.OpenParenthesisToken;
            if (depth < 0) return false;
        }
    }

    private (ImmutableArray<ExpressionSyntax> Arguments, ImmutableArray<SyntaxToken> Commas) ParseExpressionList(
        SyntaxKind closeKind)
    {
        var arguments = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        var commaTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (Current.Kind != closeKind && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            arguments.Add(ParseExpression());
            if (Current.Kind != SyntaxKind.CommaToken)
            {
                break;
            }

            commaTokens.Add(NextToken());
        }

        return (arguments.ToImmutable(), commaTokens.ToImmutable());
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        if (Current.Kind is SyntaxKind.EndOfFileToken or SyntaxKind.SemicolonToken or
            SyntaxKind.CloseParenthesisToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken or
            SyntaxKind.CommaToken)
        {
            Diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, SyntaxKind.IdentifierToken);
            return new MissingExpressionSyntax(new SyntaxToken(
                SyntaxKind.IdentifierToken,
                new TextLocation(Current.Location.Source, new TextSpan(Current.Location.Span.Start, 0)),
                string.Empty,
                IsMissing: true));
        }

        if (Current.Kind == SyntaxKind.CastKeyword)
        {
            SyntaxToken keyword = NextToken();
            SyntaxToken less = MatchToken(SyntaxKind.LessToken);
            TypeSyntax type = ParseType();
            SyntaxToken greater = MatchTypeGreaterToken();
            SyntaxToken open = MatchToken(SyntaxKind.OpenParenthesisToken);
            ExpressionSyntax expression = ParseExpression();
            SyntaxToken close = MatchToken(SyntaxKind.CloseParenthesisToken);
            return new CastExpressionSyntax(keyword, less, type, greater, open, expression, close);
        }

        if (Current.Kind is SyntaxKind.SizeOfKeyword or SyntaxKind.AlignOfKeyword or SyntaxKind.OffsetOfKeyword)
        {
            SyntaxToken keyword = NextToken();
            SyntaxToken open = MatchToken(SyntaxKind.OpenParenthesisToken);
            TypeSyntax type = ParseType();
            SyntaxToken? comma = null;
            SyntaxToken? field = null;
            if (keyword.Kind == SyntaxKind.OffsetOfKeyword)
            {
                comma = MatchToken(SyntaxKind.CommaToken);
                field = MatchToken(SyntaxKind.IdentifierToken);
            }
            SyntaxToken close = MatchToken(SyntaxKind.CloseParenthesisToken);
            return new TypeLayoutExpressionSyntax(keyword, open, type, comma, field, close);
        }

        if (Current.Kind == SyntaxKind.NewKeyword)
        {
            SyntaxToken newKeyword = NextToken();
            TypeSyntax type = ParseType(allowArraySuffix: false);

            if (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                SyntaxToken openBracket = NextToken();
                (ImmutableArray<ExpressionSyntax> dimensions, ImmutableArray<SyntaxToken> dimensionCommas) = ParseExpressionList(SyntaxKind.CloseBracketToken);
                SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
                type = ParseAllocationElementSuffixes(type);
                return new NewExpressionSyntax(
                    newKeyword,
                    type,
                    openBracket,
                    dimensions,
                    dimensionCommas,
                    closeBracket);
            }

            SyntaxKind closeKind = Current.Kind switch
            {
                SyntaxKind.OpenBraceToken => SyntaxKind.CloseBraceToken,
                _ => SyntaxKind.CloseParenthesisToken,
            };
            SyntaxToken openDelimiter = closeKind == SyntaxKind.CloseBraceToken
                ? MatchToken(SyntaxKind.OpenBraceToken)
                : MatchToken(SyntaxKind.OpenParenthesisToken);
            (ImmutableArray<ExpressionSyntax> arguments, ImmutableArray<SyntaxToken> commas) =
                ParseExpressionList(closeKind);
            SyntaxToken closeDelimiter = MatchToken(closeKind);
            return new NewExpressionSyntax(
                newKeyword,
                type,
                openDelimiter,
                arguments,
                commas,
                closeDelimiter);
        }

        if (Current.Kind == SyntaxKind.FreeKeyword)
        {
            SyntaxToken freeKeyword = NextToken();
            SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
            ExpressionSyntax pointer = ParseExpression();
            SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
            return new FreeExpressionSyntax(freeKeyword, openParenthesis, pointer, closeParenthesis);
        }

        if ((IsBuiltinTypeKeyword(Current.Kind) &&
             Peek(1).Kind is SyntaxKind.OpenBracketToken or SyntaxKind.StarToken) ||
            Current.Kind == SyntaxKind.AtomicKeyword)
        {
            TypeSyntax elementType = ParseType(allowArraySuffix: false);
            SyntaxToken openBracket = MatchToken(SyntaxKind.OpenBracketToken);
            (ImmutableArray<ExpressionSyntax> dimensions, ImmutableArray<SyntaxToken> commas) = ParseExpressionList(SyntaxKind.CloseBracketToken);
            SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
            elementType = ParseAllocationElementSuffixes(elementType);
            return new StackArrayCreationExpressionSyntax(elementType, openBracket, dimensions, commas, closeBracket);
        }

        if (Current.Kind == SyntaxKind.IdentifierToken && IsQualifiedNameFollowedBy(SyntaxKind.OpenBraceToken))
        {
            TypeSyntax type = ParseType(allowArraySuffix: false);
            SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
            (ImmutableArray<ExpressionSyntax> arguments, ImmutableArray<SyntaxToken> commas) =
                ParseExpressionList(SyntaxKind.CloseBraceToken);
            SyntaxToken closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
            return new StructPositionalConstructionExpressionSyntax(
                type,
                openBrace,
                arguments,
                commas,
                closeBrace);
        }

        if (Current.Kind == SyntaxKind.OpenParenthesisToken)
        {
            SyntaxToken openParenthesis = NextToken();
            ExpressionSyntax expression = ParseExpression();
            SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
            return new ParenthesizedExpressionSyntax(openParenthesis, expression, closeParenthesis);
        }

        if (Current.Kind is
            SyntaxKind.IntegerLiteralToken or
            SyntaxKind.FloatingPointLiteralToken or
            SyntaxKind.StringLiteralToken or
            SyntaxKind.TrueKeyword or
            SyntaxKind.FalseKeyword or
            SyntaxKind.NullKeyword)
        {
            return new LiteralExpressionSyntax(NextToken());
        }

        if (Current.Kind == SyntaxKind.IdentifierToken)
        {
            return new NameExpressionSyntax(NextToken());
        }

        if (Current.Kind == SyntaxKind.ThisKeyword)
        {
            return new ThisExpressionSyntax(NextToken());
        }

        SyntaxToken unexpected = NextToken();
        Diagnostics.ReportUnexpectedToken(
            unexpected.Location,
            unexpected.Kind,
            SyntaxKind.IdentifierToken);
        var missing = new SyntaxToken(
            SyntaxKind.IdentifierToken,
            new TextLocation(unexpected.Location.Source, new TextSpan(unexpected.Location.Span.Start, 0)),
            string.Empty,
            IsMissing: true);
        return new MissingExpressionSyntax(missing);
    }

    private TypeSyntax ParseAllocationElementSuffixes(TypeSyntax type) =>
        ParseArrayTypeSuffixes(type, allocation: true);

    private bool IsVariableDeclaration()
    {
        int offset = Current.Kind is SyntaxKind.ConstKeyword or SyntaxKind.ReadonlyKeyword ? 1 : 0;
        SyntaxKind firstKind = Peek(offset).Kind;
        if (!SyntaxFacts.IsTypeName(firstKind))
        {
            return false;
        }

        offset++;
        if (firstKind == SyntaxKind.IdentifierToken)
        {
            while (Peek(offset).Kind == SyntaxKind.DotToken &&
                   Peek(offset + 1).Kind == SyntaxKind.IdentifierToken)
            {
                offset += 2;
            }
        }

        if (Peek(offset).Kind == SyntaxKind.LessToken)
        {
            int depth = 0;
            do
            {
                SyntaxKind kind = Peek(offset++).Kind;
                if (kind == SyntaxKind.LessToken) depth++;
                else if (kind == SyntaxKind.GreaterToken) depth--;
                else if (kind == SyntaxKind.GreaterGreaterToken) depth -= 2;
                else if (kind is SyntaxKind.EndOfFileToken or SyntaxKind.SemicolonToken or
                         SyntaxKind.OpenBraceToken or SyntaxKind.CloseBraceToken) return false;
                else if (!SyntaxFacts.IsTypeName(kind) && kind is not (SyntaxKind.ReadonlyKeyword or
                         SyntaxKind.ConstKeyword or SyntaxKind.DotToken or SyntaxKind.CommaToken or
                         SyntaxKind.StarToken or SyntaxKind.AmpersandToken or SyntaxKind.OpenBracketToken or
                         SyntaxKind.CloseBracketToken)) return false;
            } while (depth > 0);
            if (depth != 0) return false;
        }

        while (Peek(offset).Kind == SyntaxKind.StarToken)
        {
            offset++;
        }

        if (Peek(offset).Kind == SyntaxKind.ReadonlyKeyword) offset++;

        if (Peek(offset).Kind == SyntaxKind.AmpersandToken)
        {
            offset++;
        }

        while (Peek(offset).Kind == SyntaxKind.OpenBracketToken)
        {
            offset++;
            while (Peek(offset).Kind == SyntaxKind.CommaToken) offset++;
            if (Peek(offset).Kind == SyntaxKind.IntegerLiteralToken)
            {
                offset++;
            }

            if (Peek(offset).Kind != SyntaxKind.CloseBracketToken)
            {
                return false;
            }

            offset++;
        }

        return Peek(offset).Kind == SyntaxKind.IdentifierToken;
    }

    private bool IsQualifiedNameFollowedBy(SyntaxKind kind)
    {
        int offset = 0;
        if (Peek(offset).Kind != SyntaxKind.IdentifierToken)
        {
            return false;
        }

        offset++;
        while (Peek(offset).Kind == SyntaxKind.DotToken &&
               Peek(offset + 1).Kind == SyntaxKind.IdentifierToken)
        {
            offset += 2;
        }

        return Peek(offset).Kind == kind;
    }

    private static bool IsBuiltinTypeKeyword(SyntaxKind kind) => kind is
        SyntaxKind.BoolKeyword or
        SyntaxKind.ByteKeyword or
        SyntaxKind.SByteKeyword or
        SyntaxKind.ShortKeyword or
        SyntaxKind.UShortKeyword or
        SyntaxKind.IntKeyword or
        SyntaxKind.UIntKeyword or
        SyntaxKind.LongKeyword or
        SyntaxKind.ULongKeyword or
        SyntaxKind.FloatKeyword or
        SyntaxKind.DoubleKeyword or
        SyntaxKind.NIntKeyword or
        SyntaxKind.NUIntKeyword or
        SyntaxKind.CLongKeyword or
        SyntaxKind.CULongKeyword;

    private SyntaxToken MatchToken(SyntaxKind kind)
    {
        if (Current.Kind == kind)
        {
            return NextToken();
        }

        Diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
        return new SyntaxToken(
            kind,
            new TextLocation(Current.Location.Source, new TextSpan(Current.Location.Span.Start, 0)),
            string.Empty,
            IsMissing: true);
    }

    private SyntaxToken NextToken()
    {
        SyntaxToken current = Current;
        _position++;
        return current;
    }

    private SyntaxToken Peek(int offset)
    {
        int index = _position + offset;
        return index >= _tokens.Count ? _tokens[^1] : _tokens[index];
    }
}
