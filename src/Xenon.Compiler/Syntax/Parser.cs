using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

internal sealed class Parser
{
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private int _position;

    public Parser(ImmutableArray<SyntaxToken> tokens)
    {
        _tokens = tokens.Where(token => token.Kind != SyntaxKind.BadToken).ToImmutableArray();
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
                Diagnostics.Report(Current.Location, "using directives must appear before the namespace declaration");
                ParseUsingDirective();
            }
            else
            {
                members.Add(Current.Kind switch
                {
                    SyntaxKind.StructKeyword => ParseStructDeclaration(),
                    SyntaxKind.InterfaceKeyword => ParseInterfaceDeclaration(),
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

    private StructDeclarationSyntax ParseStructDeclaration()
    {
        SyntaxToken structKeyword = MatchToken(SyntaxKind.StructKeyword);
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        (SyntaxToken? colon, ImmutableArray<TypeSyntax> bases, ImmutableArray<SyntaxToken> baseCommas) = ParseBaseTypeList();
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var members = ImmutableArray.CreateBuilder<StructMemberDeclarationSyntax>();

        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            if (Current.Kind == SyntaxKind.ConstKeyword)
            {
                members.Add(ParseStructConstantDeclaration());
                continue;
            }
            (SyntaxToken? accessModifier, SyntaxToken? @static, SyntaxToken? @virtual, SyntaxToken? @override, SyntaxToken? @abstract, SyntaxToken? @readonly) = ParseStructMemberModifiers();

            if (Current.Kind == SyntaxKind.TildeToken)
            {
                members.Add(ParseDestructorDeclaration(accessModifier, @virtual));
            }
            else if (Current.Kind == SyntaxKind.IdentifierToken &&
                     string.Equals(Current.Text, identifier.Text, StringComparison.Ordinal) &&
                     Peek(1).Kind == SyntaxKind.OpenParenthesisToken)
            {
                members.Add(ParseConstructorDeclaration(accessModifier));
            }
            else
            {
                TypeSyntax type = ParseType();
                if (Current.Kind == SyntaxKind.ThisKeyword)
                {
                    members.Add(ParseIndexerDeclaration(accessModifier, @static, @virtual, @override, @abstract, @readonly, type));
                    continue;
                }
                SyntaxToken memberIdentifier = MatchToken(SyntaxKind.IdentifierToken);
                if (Current.Kind == SyntaxKind.OpenParenthesisToken)
                {
                    members.Add(ParseMethodDeclaration(accessModifier, @static, @virtual, @override, @abstract, @readonly, type, memberIdentifier));
                }
                else if (Current.Kind == SyntaxKind.OpenBraceToken)
                {
                    members.Add(ParsePropertyDeclaration(accessModifier, @static, @virtual, @override, @abstract, @readonly, type, memberIdentifier));
                }
                else
                {
                    SyntaxToken? equals = Current.Kind == SyntaxKind.EqualsToken ? NextToken() : null;
                    ExpressionSyntax? initializer = equals is null ? null : ParseExpression();
                    SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                    members.Add(new FieldDeclarationSyntax(accessModifier, @static, @readonly, type, memberIdentifier, equals, initializer, semicolon));
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
            colon,
            bases,
            baseCommas,
            openBrace,
            members.ToImmutable(),
            closeBrace);
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

    private StructConstantDeclarationSyntax ParseStructConstantDeclaration()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.ConstKeyword);
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken equals = MatchToken(SyntaxKind.EqualsToken);
        ExpressionSyntax initializer = ParseExpression();
        return new StructConstantDeclarationSyntax(keyword, type, identifier, equals, initializer, MatchToken(SyntaxKind.SemicolonToken));
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
                Diagnostics.Report(Current.Location, "expected 'get' or 'set' property accessor");
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
            SyntaxToken keyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                ? NextToken()
                : MatchToken(SyntaxKind.GetKeyword);
            if (Current.Kind == SyntaxKind.SemicolonToken)
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, null, NextToken()));
            else
                accessors.Add(new PropertyAccessorDeclarationSyntax(keyword, ParseBlockStatement(), null));
        }
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
            baseKeyword = MatchToken(SyntaxKind.BaseKeyword);
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
            SyntaxToken? readonlyKeyword = Current.Kind == SyntaxKind.ReadonlyKeyword
                ? NextToken()
                : null;
            TypeSyntax returnType = ParseType();
            if (Current.Kind == SyntaxKind.ThisKeyword)
            {
                SyntaxToken thisKeyword = NextToken();
                SyntaxToken indexOpen = MatchToken(SyntaxKind.OpenBracketToken);
                (ImmutableArray<ParameterSyntax> indexParameters, ImmutableArray<SyntaxToken> indexCommas) =
                    ParseParameterList(SyntaxKind.CloseBracketToken);
                SyntaxToken indexClose = MatchToken(SyntaxKind.CloseBracketToken);
                SyntaxToken accessorOpen = MatchToken(SyntaxKind.OpenBraceToken);
                var indexAccessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
                while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
                {
                    SyntaxToken accessorKeyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                        ? NextToken()
                        : MatchToken(SyntaxKind.GetKeyword);
                    indexAccessors.Add(new PropertyAccessorDeclarationSyntax(
                        accessorKeyword,
                        null,
                        MatchToken(SyntaxKind.SemicolonToken)));
                }
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
                SyntaxToken open = MatchToken(SyntaxKind.OpenParenthesisToken);
                (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> methodCommas) = ParseParameterList();
                SyntaxToken close = MatchToken(SyntaxKind.CloseParenthesisToken);
                SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                methods.Add(new InterfaceMethodDeclarationSyntax(readonlyKeyword, returnType, name, open, parameters, methodCommas, close, semicolon));
            }
            else
            {
                SyntaxToken propertyOpen = MatchToken(SyntaxKind.OpenBraceToken);
                var accessors = ImmutableArray.CreateBuilder<PropertyAccessorDeclarationSyntax>();
                while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
                {
                    SyntaxToken accessorKeyword = Current.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword
                        ? NextToken()
                        : MatchToken(SyntaxKind.GetKeyword);
                    SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
                    accessors.Add(new PropertyAccessorDeclarationSyntax(accessorKeyword, null, semicolon));
                }
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

    private (SyntaxToken? Access, SyntaxToken? Static, SyntaxToken? Virtual, SyntaxToken? Override, SyntaxToken? Abstract, SyntaxToken? Readonly) ParseStructMemberModifiers()
    {
        SyntaxToken? access = null, @static = null, @virtual = null, @override = null, @abstract = null, @readonly = null;
        while (Current.Kind is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.StaticKeyword or SyntaxKind.VirtualKeyword or SyntaxKind.OverrideKeyword or SyntaxKind.AbstractKeyword or SyntaxKind.ReadonlyKeyword)
        {
            if (Current.Kind == SyntaxKind.ReadonlyKeyword && @readonly is not null)
                break;
            SyntaxToken modifier = NextToken();
            switch (modifier.Kind)
            {
                case SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword: access ??= modifier; break;
                case SyntaxKind.StaticKeyword: @static ??= modifier; break;
                case SyntaxKind.VirtualKeyword: @virtual ??= modifier; break;
                case SyntaxKind.OverrideKeyword: @override ??= modifier; break;
                case SyntaxKind.AbstractKeyword: @abstract ??= modifier; break;
                case SyntaxKind.ReadonlyKeyword: @readonly ??= modifier; break;
            }
        }
        return (access, @static, @virtual, @override, @abstract, @readonly);
    }

    private DestructorDeclarationSyntax ParseDestructorDeclaration(SyntaxToken? accessModifier, SyntaxToken? @virtual)
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
            body);
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
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ParameterSyntax> parameters, ImmutableArray<SyntaxToken> commaTokens) = ParseParameterList();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
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
            openParenthesis,
            parameters,
            commaTokens,
            closeParenthesis,
            body,
            semicolon);
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
            ? NextToken()
            : MatchToken(SyntaxKind.IdentifierToken);
        nameParts.Add(firstName);

        if (firstName.Kind == SyntaxKind.IdentifierToken)
        {
            while (Current.Kind == SyntaxKind.DotToken)
            {
                dotTokens.Add(NextToken());
                nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
            }
        }

        var pointerTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (Current.Kind == SyntaxKind.StarToken)
        {
            pointerTokens.Add(NextToken());
        }

        SyntaxToken? referenceToken = Current.Kind == SyntaxKind.AmpersandToken
            ? NextToken()
            : null;

        SyntaxToken? openBracket = null;
        SyntaxToken? closeBracket = null;
        if (allowArraySuffix && Current.Kind == SyntaxKind.OpenBracketToken)
        {
            openBracket = NextToken();
            if (Current.Kind != SyntaxKind.CloseBracketToken)
            {
                Diagnostics.Report(
                    Current.Location,
                    "fixed-size array type syntax is not supported; use 'T[]' and initialize it with 'T[n]' or 'new T[n]'");

                while (Current.Kind is not SyntaxKind.CloseBracketToken and not SyntaxKind.EndOfFileToken)
                {
                    NextToken();
                }
            }

            closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
        }

        return new TypeSyntax(
            constKeyword,
            readonlyKeyword,
            nameParts.ToImmutable(),
            dotTokens.ToImmutable(),
            pointerTokens.ToImmutable(),
            referenceToken,
            openBracket,
            closeBracket);
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

    private ExpressionSyntax ParseExpression() => ParseAssignmentExpression();

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

        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
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
                expression = ParseCallExpression(expression);
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

    private CallExpressionSyntax ParseCallExpression(ExpressionSyntax target)
    {
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        (ImmutableArray<ExpressionSyntax> arguments, ImmutableArray<SyntaxToken> commas) =
            ParseExpressionList(SyntaxKind.CloseParenthesisToken);
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        return new CallExpressionSyntax(
            target,
            openParenthesis,
            arguments,
            commas,
            closeParenthesis);
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
        if (Current.Kind == SyntaxKind.CastKeyword)
        {
            SyntaxToken keyword = NextToken();
            SyntaxToken less = MatchToken(SyntaxKind.LessToken);
            TypeSyntax type = ParseType();
            SyntaxToken greater = MatchToken(SyntaxKind.GreaterToken);
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
                ExpressionSyntax length = ParseExpression();
                SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
                return new NewExpressionSyntax(
                    newKeyword,
                    type,
                    openBracket,
                    [length],
                    [],
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

        if (IsBuiltinTypeKeyword(Current.Kind) && Peek(1).Kind == SyntaxKind.OpenBracketToken)
        {
            TypeSyntax elementType = ParseType(allowArraySuffix: false);
            SyntaxToken openBracket = MatchToken(SyntaxKind.OpenBracketToken);
            ExpressionSyntax length = ParseExpression();
            SyntaxToken closeBracket = MatchToken(SyntaxKind.CloseBracketToken);
            return new StackArrayCreationExpressionSyntax(elementType, openBracket, length, closeBracket);
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
        return new NameExpressionSyntax(missing);
    }

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

        while (Peek(offset).Kind == SyntaxKind.StarToken)
        {
            offset++;
        }

        if (Peek(offset).Kind == SyntaxKind.AmpersandToken)
        {
            offset++;
        }

        if (Peek(offset).Kind == SyntaxKind.OpenBracketToken)
        {
            offset++;
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
        return index >= _tokens.Length ? _tokens[^1] : _tokens[index];
    }
}
