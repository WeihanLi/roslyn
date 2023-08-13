﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.UseCollectionInitializer
{
    using static UseCollectionInitializerHelpers;
    using static UpdateObjectCreationHelpers;

    internal abstract class AbstractUseCollectionInitializerAnalyzer<
        TExpressionSyntax,
        TStatementSyntax,
        TObjectCreationExpressionSyntax,
        TMemberAccessExpressionSyntax,
        TInvocationExpressionSyntax,
        TExpressionStatementSyntax,
        TVariableDeclaratorSyntax,
        TAnalyzer> : AbstractObjectCreationExpressionAnalyzer<
            TExpressionSyntax,
            TStatementSyntax,
            TObjectCreationExpressionSyntax,
            TVariableDeclaratorSyntax,
            Match<TStatementSyntax>>, IDisposable
        where TExpressionSyntax : SyntaxNode
        where TStatementSyntax : SyntaxNode
        where TObjectCreationExpressionSyntax : TExpressionSyntax
        where TMemberAccessExpressionSyntax : TExpressionSyntax
        where TInvocationExpressionSyntax : TExpressionSyntax
        where TExpressionStatementSyntax : TStatementSyntax
        where TVariableDeclaratorSyntax : SyntaxNode
        where TAnalyzer : AbstractUseCollectionInitializerAnalyzer<
            TExpressionSyntax,
            TStatementSyntax,
            TObjectCreationExpressionSyntax,
            TMemberAccessExpressionSyntax,
            TInvocationExpressionSyntax,
            TExpressionStatementSyntax,
            TVariableDeclaratorSyntax,
            TAnalyzer>, new()
    {
        private static readonly ObjectPool<TAnalyzer> s_pool = SharedPools.Default<TAnalyzer>();

        protected abstract bool IsComplexElementInitializer(SyntaxNode expression);
        protected abstract bool HasExistingInvalidInitializerForCollection(TObjectCreationExpressionSyntax objectCreation);

        protected abstract IUpdateExpressionSyntaxHelper<TExpressionSyntax, TStatementSyntax> SyntaxHelper { get; }

        public static TAnalyzer Allocate()
            => s_pool.Allocate();

        public void Dispose()
        {
            this.Clear();
            s_pool.Free((TAnalyzer)this);
        }

        public ImmutableArray<Match<TStatementSyntax>> Analyze(
            SemanticModel semanticModel,
            ISyntaxFacts syntaxFacts,
            TObjectCreationExpressionSyntax objectCreationExpression,
            bool analyzeForCollectionExpression,
            CancellationToken cancellationToken)
        {
            var statement = objectCreationExpression.FirstAncestorOrSelf<TStatementSyntax>()!;

            var state =
                TryInitializeVariableDeclarationCase(semanticModel, syntaxFacts, (TExpressionSyntax)objectCreationExpression, statement, cancellationToken) ??
                TryInitializeAssignmentCase(semanticModel, syntaxFacts, (TExpressionSyntax)objectCreationExpression, statement, cancellationToken);

            if (state is null)
                return default;

            this.Initialize(state.Value, objectCreationExpression, analyzeForCollectionExpression);
            var result = this.AnalyzeWorker(cancellationToken);

            // If analysis failed entirely, immediately bail out.
            if (result.IsDefault)
                return default;

            // Analysis succeeded, but the result may be empty or non empty.
            //
            // For collection expressions, it's fine for this result to be empty.  In other words, it's ok to offer
            // changing `new List<int>() { 1 }` (on its own) to `[1]`.
            //
            // However, for collection initializers we always want at least one element to add to the initializer.  In
            // other words, we don't want to suggest changing `new List<int>()` to `new List<int>() { }` as that's just
            // noise.  So convert empty results to an invalid result here.
            if (analyzeForCollectionExpression)
                return result;

            // Downgrade an empty result to a failure for the normal collection-initializer case.
            return result.IsEmpty ? default : result;
        }

        protected override bool TryAddMatches(
            ArrayBuilder<Match<TStatementSyntax>> matches, CancellationToken cancellationToken)
        {
            var seenInvocation = false;
            var seenIndexAssignment = false;

            var initializer = this.SyntaxFacts.GetInitializerOfBaseObjectCreationExpression(_objectCreationExpression);
            if (initializer != null)
            {
                var initializerExpressions = this.SyntaxFacts.GetExpressionsOfObjectCollectionInitializer(initializer);
                if (initializerExpressions is [var firstInit, ..])
                {
                    // if we have an object creation, and it *already* has an initializer in it (like `new T { { x, y } }`)
                    // this can't legally become a collection expression.
                    if (_analyzeForCollectionExpression && this.IsComplexElementInitializer(firstInit))
                        return false;

                    seenIndexAssignment = this.SyntaxFacts.IsElementAccessInitializer(firstInit);
                    seenInvocation = !seenIndexAssignment;

                    // An indexer can't be used with a collection expression.  So fail out immediately if we see that.
                    if (seenIndexAssignment && _analyzeForCollectionExpression)
                        return false;
                }
            }

            foreach (var statement in this.State.GetSubsequentStatements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = TryAnalyzeStatement(statement, ref seenInvocation, ref seenIndexAssignment, cancellationToken);
                if (match is null)
                    break;

                matches.Add(match.Value);
            }

            return true;
        }

        private Match<TStatementSyntax>? TryAnalyzeStatement(
            TStatementSyntax statement, ref bool seenInvocation, ref bool seenIndexAssignment, CancellationToken cancellationToken)
        {
            return _analyzeForCollectionExpression
                ? State.TryAnalyzeStatementForCollectionExpression(this.SyntaxHelper, statement, cancellationToken)
                : TryAnalyzeStatementForCollectionInitializer(statement, ref seenInvocation, ref seenIndexAssignment, cancellationToken);
        }

        private Match<TStatementSyntax>? TryAnalyzeStatementForCollectionInitializer(
            TStatementSyntax statement, ref bool seenInvocation, ref bool seenIndexAssignment, CancellationToken cancellationToken)
        {
            // At least one of these has to be false.
            Contract.ThrowIfTrue(seenInvocation && seenIndexAssignment);

            if (statement is not TExpressionStatementSyntax expressionStatement)
                return null;

            // Can't mix Adds and indexing.
            if (!seenIndexAssignment)
            {
                // Look for a call to Add or AddRange
                if (TryAnalyzeInvocation(
                        expressionStatement,
                        addName: WellKnownMemberNames.CollectionInitializerAddMethodName,
                        requiredArgumentName: null,
                        cancellationToken,
                        out var instance) &&
                    this.State.ValuePatternMatches(instance))
                {
                    seenInvocation = true;
                    return new Match<TStatementSyntax>(expressionStatement, UseSpread: false);
                }
            }

            if (!seenInvocation)
            {
                if (TryAnalyzeIndexAssignment(expressionStatement, cancellationToken, out var instance) &&
                    this.State.ValuePatternMatches(instance))
                {
                    seenIndexAssignment = true;
                    return new Match<TStatementSyntax>(expressionStatement, UseSpread: false);
                }
            }

            return null;
        }

        protected override bool ShouldAnalyze(CancellationToken cancellationToken)
        {
            if (this.HasExistingInvalidInitializerForCollection(_objectCreationExpression))
                return false;

            var type = this.SemanticModel.GetTypeInfo(_objectCreationExpression, cancellationToken).Type;
            if (type == null)
                return false;

            var addMethods = this.SemanticModel.LookupSymbols(
                _objectCreationExpression.SpanStart,
                container: type,
                name: WellKnownMemberNames.CollectionInitializerAddMethodName,
                includeReducedExtensionMethods: true);

            return addMethods.Any(static m => m is IMethodSymbol methodSymbol && methodSymbol.Parameters.Any());
        }

        private bool TryAnalyzeIndexAssignment(
            TExpressionStatementSyntax statement,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out TExpressionSyntax? instance)
        {
            instance = null;
            if (!this.SyntaxFacts.SupportsIndexingInitializer(statement.SyntaxTree.Options))
                return false;

            if (!this.SyntaxFacts.IsSimpleAssignmentStatement(statement))
                return false;

            this.SyntaxFacts.GetPartsOfAssignmentStatement(statement, out var left, out var right);

            if (!this.SyntaxFacts.IsElementAccessExpression(left))
                return false;

            // If we're initializing a variable, then we can't reference that variable on the right 
            // side of the initialization.  Rewriting this into a collection initializer would lead
            // to a definite-assignment error.
            if (this.State.ExpressionContainsValuePatternOrReferencesInitializedSymbol(right, cancellationToken))
                return false;

            // Can't reference the variable being initialized in the arguments of the indexing expression.
            this.SyntaxFacts.GetPartsOfElementAccessExpression(left, out var elementInstance, out var argumentList);
            var elementAccessArguments = this.SyntaxFacts.GetArgumentsOfArgumentList(argumentList);
            foreach (var argument in elementAccessArguments)
            {
                if (this.State.ExpressionContainsValuePatternOrReferencesInitializedSymbol(argument, cancellationToken))
                    return false;

                // An index/range expression implicitly references the value being initialized.  So it cannot be used in the
                // indexing expression.
                var argExpression = this.SyntaxFacts.GetExpressionOfArgument(argument);
                argExpression = this.SyntaxFacts.WalkDownParentheses(argExpression);

                if (this.SyntaxFacts.IsIndexExpression(argExpression) || this.SyntaxFacts.IsRangeExpression(argExpression))
                    return false;
            }

            instance = elementInstance as TExpressionSyntax;
            return instance != null;
        }

        private bool TryAnalyzeInvocation(
            TExpressionStatementSyntax statement,
            string addName,
            string? requiredArgumentName,
            CancellationToken cancellationToken,
            [NotNullWhen(true)] out TExpressionSyntax? instance)
        {
            instance = null;
            if (this.SyntaxFacts.GetExpressionOfExpressionStatement(statement) is not TInvocationExpressionSyntax invocationExpression)
                return false;

            var arguments = this.SyntaxFacts.GetArgumentsOfInvocationExpression(invocationExpression);
            if (arguments.Count < 1)
                return false;

            // Collection expressions can only call the single argument Add/AddRange methods on a type.
            // So if we don't have exactly one argument, fail out.
            if (_analyzeForCollectionExpression && arguments.Count != 1)
                return false;

            if (requiredArgumentName != null && arguments.Count != 1)
                return false;

            foreach (var argument in arguments)
            {
                if (!this.SyntaxFacts.IsSimpleArgument(argument))
                    return false;

                var argumentExpression = this.SyntaxFacts.GetExpressionOfArgument(argument);
                if (this.State.ExpressionContainsValuePatternOrReferencesInitializedSymbol(argumentExpression, cancellationToken))
                    return false;

                // VB allows for a collection initializer to be an argument.  i.e. `Goo({a, b, c})`.  This argument
                // cannot be used in an outer collection initializer as it would change meaning.  i.e.:
                //
                //      new List(Of IEnumerable(Of String)) { { a, b, c } }
                //
                // is not legal.  That's because instead of adding `{ a, b, c }` as a single element to the list, VB
                // instead looks for an 3-argument `Add` method to invoke on `List<T>` (which clearly fails).
                if (this.SyntaxFacts.SyntaxKinds.CollectionInitializerExpression == argumentExpression.RawKind)
                    return false;

                // If the caller is requiring a particular argument name, then validate that is what this argument
                // is referencing.
                if (requiredArgumentName != null)
                {
                    if (!this.SyntaxFacts.IsIdentifierName(argumentExpression))
                        return false;

                    this.SyntaxFacts.GetNameAndArityOfSimpleName(argumentExpression, out var suppliedName, out _);
                    if (requiredArgumentName != suppliedName)
                        return false;
                }
            }

            if (this.SyntaxFacts.GetExpressionOfInvocationExpression(invocationExpression) is not TMemberAccessExpressionSyntax memberAccess)
                return false;

            if (!this.SyntaxFacts.IsSimpleMemberAccessExpression(memberAccess))
                return false;

            this.SyntaxFacts.GetPartsOfMemberAccessExpression(memberAccess, out var localInstance, out var memberName);
            this.SyntaxFacts.GetNameAndArityOfSimpleName(memberName, out var name, out var arity);

            if (arity != 0 || !Equals(name, addName))
                return false;

            instance = localInstance as TExpressionSyntax;
            return instance != null;
        }
    }
}
