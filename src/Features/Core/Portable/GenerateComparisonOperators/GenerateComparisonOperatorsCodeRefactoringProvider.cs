﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.GenerateComparisonOperators
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, LanguageNames.VisualBasic), Shared]
    internal class GenerateComparisonOperatorsCodeRefactoringProvider : CodeRefactoringProvider
    {
        private const string LeftName = "left";
        private const string RightName = "right";

        [ImportingConstructor]
        public GenerateComparisonOperatorsCodeRefactoringProvider()
        {
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;

            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            // We offer the refactoring when the user is either on the header of a class/struct,
            // or if they're between any members of a class/struct and are on a blank line.
            if (!syntaxFacts.IsOnTypeHeader(root, textSpan.Start, fullHeader: true, out var typeDeclaration) &&
                !syntaxFacts.IsBetweenTypeMembers(sourceText, root, textSpan.Start, out typeDeclaration))
            {
                return;
            }

            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var compilation = semanticModel.Compilation;

            var comparableType = compilation.GetTypeByMetadataName(typeof(IComparable<>).FullName);
            if (comparableType == null)
                return;

            var containingType = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
            if (containingType == null)
                return;

            using var _1 = ArrayBuilder<INamedTypeSymbol>.GetInstance(out var missingComparableTypes);

            foreach (var iface in containingType.Interfaces)
            {
                if (!iface.OriginalDefinition.Equals(comparableType))
                    continue;

                var comparedType = comparableType.TypeArguments[0];
                if (comparedType.IsErrorType())
                    continue;

                var compareMethod = TryGetCompareMethodImpl(containingType, iface);
                if (compareMethod == null)
                    continue;

                if (HasComparisonOperators(containingType, comparedType))
                    continue;

                missingComparableTypes.Add(iface);
            }

            if (missingComparableTypes.Count == 0)
                return;

            if (missingComparableTypes.Count == 1)
            {
                var missingType = missingComparableTypes[0];
                context.RegisterRefactoring(new MyCodeAction(
                    FeaturesResources.Generate_comparison_operators,
                    c => GenerateComparisonOperatorsAsync(document, typeDeclaration, missingType, c)));
                return;
            }

            using var _2 = ArrayBuilder<CodeAction>.GetInstance(out var nestedActions);

            foreach (var missingType in missingComparableTypes)
            {
                var typeArg = missingType.TypeArguments[0];
                var displayString = typeArg.ToMinimalDisplayString(semanticModel, textSpan.Start);
                nestedActions.Add(new MyCodeAction(
                    string.Format(FeaturesResources.Generate_for_0, displayString),
                    c => GenerateComparisonOperatorsAsync(document, typeDeclaration, missingType, c)));
            }

            context.RegisterRefactoring(new CodeAction.CodeActionWithNestedActions(
                FeaturesResources.Generate_comparison_operators,
                nestedActions.ToImmutable(),
                isInlinable: false));
        }

        private IMethodSymbol? TryGetCompareMethodImpl(INamedTypeSymbol containingType, ITypeSymbol comparableType)
        {
            foreach (var member in comparableType.GetMembers(nameof(IComparable<int>.CompareTo)))
            {
                if (member is IMethodSymbol method)
                    return (IMethodSymbol?)containingType.FindImplementationForInterfaceMember(method);
            }

            return null;
        }

        private async Task<Document> GenerateComparisonOperatorsAsync(
            Document document,
            SyntaxNode typeDeclaration,
            INamedTypeSymbol comparableType,
            CancellationToken cancellationToken)
        {
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var containingType = (INamedTypeSymbol)semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken);
            var compareMethod = TryGetCompareMethodImpl(containingType, comparableType)!;

            var generator = document.GetRequiredLanguageService<SyntaxGenerator>();

            var codeGenService = document.GetRequiredLanguageService<ICodeGenerationService>();
            var operators = GenerateComparisonOperators(
                generator, semanticModel.Compilation, containingType, comparableType,
                GenerateLeftExpression(generator, comparableType, compareMethod));

            return await codeGenService.AddMembersAsync(
                document.Project.Solution,
                containingType,
                operators,
                new CodeGenerationOptions(
                    contextLocation: typeDeclaration.GetLocation(),
                    options: options,
                    parseOptions: typeDeclaration.SyntaxTree.Options)).ConfigureAwait(false);
        }

        private static SyntaxNode GenerateLeftExpression(
            SyntaxGenerator generator,
            INamedTypeSymbol comparableType,
            IMethodSymbol compareMethod)
        {
            var thisExpression = generator.IdentifierName(LeftName);
            var generateCast = compareMethod != null && compareMethod.DeclaredAccessibility != Accessibility.Public;
            return generateCast
                ? generator.CastExpression(comparableType, thisExpression)
                : thisExpression;
        }

        private ImmutableArray<IMethodSymbol> GenerateComparisonOperators(
            SyntaxGenerator generator,
            Compilation compilation,
            INamedTypeSymbol containingType,
            INamedTypeSymbol comparableType,
            SyntaxNode thisExpression)
        {
            using var _ = ArrayBuilder<IMethodSymbol>.GetInstance(out var operators);

            var boolType = compilation.GetSpecialType(SpecialType.System_Boolean);
            var comparedType = comparableType.TypeArguments[0];

            var parameters = ImmutableArray.Create(
                CodeGenerationSymbolFactory.CreateParameterSymbol(containingType, LeftName),
                CodeGenerationSymbolFactory.CreateParameterSymbol(comparedType, RightName));

            operators.Add(CreateOperator(generator, CodeGenerationOperatorKind.LessThan, boolType, parameters, thisExpression));
            operators.Add(CreateOperator(generator, CodeGenerationOperatorKind.LessThanOrEqual, boolType, parameters, thisExpression));
            operators.Add(CreateOperator(generator, CodeGenerationOperatorKind.GreaterThan, boolType, parameters, thisExpression));
            operators.Add(CreateOperator(generator, CodeGenerationOperatorKind.GreaterThanOrEqual, boolType, parameters, thisExpression));

            //operators.Add(CodeGenerationSymbolFactory.CreateOperatorSymbol(
            //    attributes: default,
            //    Accessibility.Public,
            //    DeclarationModifiers.Static,
            //    boolType,
            //    CodeGenerationOperatorKind.LessThan,
            //    parameters,
            //    statements));

            return operators.ToImmutable();
        }

        private IMethodSymbol CreateOperator(
            SyntaxGenerator generator,
            CodeGenerationOperatorKind kind,
            INamedTypeSymbol boolType,
            ImmutableArray<IParameterSymbol> parameters,
            SyntaxNode thisExpression)
        {
            return CodeGenerationSymbolFactory.CreateOperatorSymbol(
                attributes: default,
                Accessibility.Public,
                DeclarationModifiers.Static,
                boolType,
                kind,
                parameters,
                ImmutableArray.Create(GenerateStatement(generator, kind, thisExpression)));
        }

        private SyntaxNode GenerateStatement(
            SyntaxGenerator generator, CodeGenerationOperatorKind kind, SyntaxNode leftExpression)
        {
            var zero = generator.LiteralExpression(0);

            var compareToCall = generator.InvocationExpression(
                generator.MemberAccessExpression(leftExpression, nameof(IComparable.CompareTo)),
                generator.IdentifierName(RightName));

            var comparison = kind switch
            {
                CodeGenerationOperatorKind.LessThan => generator.LessThanExpression(compareToCall, zero),
                CodeGenerationOperatorKind.LessThanOrEqual => generator.LessThanOrEqualExpression(compareToCall, zero),
                CodeGenerationOperatorKind.GreaterThan => generator.GreaterThanExpression(compareToCall, zero),
                CodeGenerationOperatorKind.GreaterThanOrEqual => generator.GreaterThanOrEqualExpression(compareToCall, zero),
                _ => throw ExceptionUtilities.Unreachable,
            };

            return generator.ReturnStatement(comparison);
        }

        private bool HasComparisonOperators(INamedTypeSymbol containingType, ITypeSymbol comparedType)
        {
            // Look for an `operator <(... c1, ComparedType c2)` member.
            foreach (var member in containingType.GetMembers(WellKnownMemberNames.LessThanOperatorName))
            {
                if (member is IMethodSymbol method &&
                    method.Parameters.Length >= 2 &&
                    comparedType.Equals(method.Parameters[1]))
                {
                    return true;
                }
            }

            return false;
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(title, createChangedDocument)
            {
            }
        }
    }
}
