﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    /// <summary>
    /// Allows asking semantic questions about a tree of syntax nodes in a Compilation. Typically,
    /// an instance is obtained by a call to <see cref="Compilation"/>.<see
    /// cref="Compilation.GetSemanticModel(SyntaxTree, bool)"/>. 
    /// </summary>
    /// <remarks>
    /// <para>An instance of <see cref="CSharpSemanticModel"/> caches local symbols and semantic
    /// information. Thus, it is much more efficient to use a single instance of <see
    /// cref="CSharpSemanticModel"/> when asking multiple questions about a syntax tree, because
    /// information from the first question may be reused. This also means that holding onto an
    /// instance of SemanticModel for a long time may keep a significant amount of memory from being
    /// garbage collected.
    /// </para>
    /// <para>
    /// When an answer is a named symbol that is reachable by traversing from the root of the symbol
    /// table, (that is, from an <see cref="AssemblySymbol"/> of the <see cref="Compilation"/>),
    /// that symbol will be returned (i.e. the returned value will be reference-equal to one
    /// reachable from the root of the symbol table). Symbols representing entities without names
    /// (e.g. array-of-int) may or may not exhibit reference equality. However, some named symbols
    /// (such as local variables) are not reachable from the root. These symbols are visible as
    /// answers to semantic questions. When the same SemanticModel object is used, the answers
    /// exhibit reference-equality.  
    /// </para>
    /// </remarks>
    internal abstract class CSharpSemanticModel : SemanticModel
    {
        /// <summary>
        /// The compilation this object was obtained from.
        /// </summary>
        public new abstract CSharpCompilation Compilation { get; }

        /// <summary>
        /// The root node of the syntax tree that this binding is based on.
        /// </summary>
        internal new abstract CSharpSyntaxNode Root { get; }

        // Is this node one that could be successfully interrogated by GetSymbolInfo/GetTypeInfo/GetMemberGroup/GetConstantValue?
        // WARN: If isSpeculative is true, then don't look at .Parent - there might not be one.
        internal static bool CanGetSemanticInfo(CSharpSyntaxNode node, bool allowNamedArgumentName = false, bool isSpeculative = false)
        {
            Debug.Assert(node != null);

            if (!isSpeculative && IsInStructuredTriviaOtherThanCrefOrNameAttribute(node))
            {
                return false;
            }

            switch (node.Kind())
            {
                case SyntaxKind.CollectionInitializerExpression:
                case SyntaxKind.ObjectInitializerExpression:
                    //  new CollectionClass() { 1, 2, 3 }
                    //                        ~~~~~~~~~~~
                    //  OR
                    //
                    //  new ObjectClass() { field = 1, prop = 2 }
                    //                    ~~~~~~~~~~~~~~~~~~~~~~~
                    // CollectionInitializerExpression and ObjectInitializerExpression are not really expressions in the language sense.
                    // We do not allow getting the semantic info for these syntax nodes. However, we do allow getting semantic info
                    // for each of the individual initializer elements or member assignments.
                    return false;

                case SyntaxKind.ComplexElementInitializerExpression:
                    //  new Collection { 1, {2, 3} }
                    //                      ~~~~~~
                    // ComplexElementInitializerExpression are also not true expressions in the language sense, so we disallow getting the
                    // semantic info for it. However, we may be interested in getting the semantic info for the compiler generated Add
                    // method invoked with initializer expressions as arguments. Roslyn bug 11987 tracks this work item.
                    return false;

                case SyntaxKind.IdentifierName:
                    // The alias of a using directive is a declaration, so there is no semantic info - use GetDeclaredSymbol instead.
                    if (!isSpeculative && node.Parent != null && node.Parent.Kind() == SyntaxKind.NameEquals && node.Parent.Parent.Kind() == SyntaxKind.UsingDirective)
                    {
                        return false;
                    }

                    goto default;

                case SyntaxKind.OmittedTypeArgument:
                case SyntaxKind.RefExpression:
                case SyntaxKind.RefType:
                case SyntaxKind.ScopedType:
                    // These are just placeholders and are not separately meaningful.
                    return false;

                default:
                    // If we are being asked for binding info on a "missing" syntax node
                    // then there's no point in doing any work at all. For example, the user might
                    // have something like "class C { [] void M() {} }". The caller might obtain 
                    // the attribute declaration syntax and then attempt to ask for type information
                    // about the contents of the attribute. But the parser has recovered from the 
                    // missing attribute type and filled in a "missing" node in its place. There's
                    // nothing we can do with that, so let's not allow it.
                    if (node.IsMissing)
                    {
                        return false;
                    }

                    return
                        (node is ExpressionSyntax && (isSpeculative || allowNamedArgumentName || !SyntaxFacts.IsNamedArgumentName(node))) ||
                        (node is ConstructorInitializerSyntax) ||
                        (node is PrimaryConstructorBaseTypeSyntax) ||
                        (node is AttributeSyntax) ||
                        (node is CrefSyntax);
            }
        }

        #region Abstract worker methods

        /// <summary>
        /// Gets symbol information about a syntax node. This is overridden by various specializations of SemanticModel.
        /// It can assume that CheckSyntaxNode and CanGetSemanticInfo have already been called, as well as that named
        /// argument nodes have been handled.
        /// </summary>
        /// <param name="node">The syntax node to get semantic information for.</param>
        /// <param name="options">Options to control behavior.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal abstract SymbolInfo GetSymbolInfoWorker(CSharpSyntaxNode node, SymbolInfoOptions options, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets symbol information about the 'Add' method corresponding to an expression syntax <paramref name="node"/> within collection initializer.
        /// This is the worker function that is overridden in various derived kinds of Semantic Models. It can assume that 
        /// CheckSyntaxNode has already been called and the <paramref name="node"/> is in the right place in the syntax tree.
        /// </summary>
        internal abstract SymbolInfo GetCollectionInitializerSymbolInfoWorker(InitializerExpressionSyntax collectionInitializer, ExpressionSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets type information about a syntax node. This is overridden by various specializations of SemanticModel.
        /// It can assume that CheckSyntaxNode and CanGetSemanticInfo have already been called, as well as that named
        /// argument nodes have been handled.
        /// </summary>
        /// <param name="node">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal abstract CSharpTypeInfo GetTypeInfoWorker(CSharpSyntaxNode node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Binds the provided expression in the given context.
        /// </summary>
        /// <param name="position">The position to bind at.</param>
        /// <param name="expression">The expression to bind</param>
        /// <param name="bindingOption">How to speculatively bind the given expression. If this is <see cref="SpeculativeBindingOption.BindAsTypeOrNamespace"/>
        /// then the provided expression should be a <see cref="TypeSyntax"/>.</param>
        /// <param name="binder">The binder that was used to bind the given syntax.</param>
        /// <param name="crefSymbols">The symbols used in a cref. If this is not default, then the return is null.</param>
        /// <returns>The expression that was bound. If <paramref name="crefSymbols"/> is not default, this is null.</returns>
        internal abstract BoundExpression GetSpeculativelyBoundExpression(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption, out Binder binder, out ImmutableArray<Symbol> crefSymbols);

        /// <summary>
        /// Gets a list of method or indexed property symbols for a syntax node. This is overridden by various specializations of SemanticModel.
        /// It can assume that CheckSyntaxNode and CanGetSemanticInfo have already been called, as well as that named
        /// argument nodes have been handled.
        /// </summary>
        /// <param name="node">The syntax node to get semantic information for.</param>
        /// <param name="options"></param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal abstract ImmutableArray<Symbol> GetMemberGroupWorker(CSharpSyntaxNode node, SymbolInfoOptions options, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets a list of indexer symbols for a syntax node. This is overridden by various specializations of SemanticModel.
        /// It can assume that CheckSyntaxNode and CanGetSemanticInfo have already been called, as well as that named
        /// argument nodes have been handled.
        /// </summary>
        /// <param name="node">The syntax node to get semantic information for.</param>
        /// <param name="options"></param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal abstract ImmutableArray<IPropertySymbol> GetIndexerGroupWorker(CSharpSyntaxNode node, SymbolInfoOptions options, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets the constant value for a syntax node. This is overridden by various specializations of SemanticModel.
        /// It can assume that CheckSyntaxNode and CanGetSemanticInfo have already been called, as well as that named
        /// argument nodes have been handled.
        /// </summary>
        /// <param name="node">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        internal abstract Optional<object> GetConstantValueWorker(CSharpSyntaxNode node, CancellationToken cancellationToken = default(CancellationToken));

        #endregion Abstract worker methods

        #region Helpers for speculative binding

        internal Binder GetSpeculativeBinder(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            Debug.Assert(expression != null);

            position = CheckAndAdjustPosition(position);

            if (bindingOption == SpeculativeBindingOption.BindAsTypeOrNamespace)
            {
                if (!(expression is TypeSyntax))
                {
                    return null;
                }
            }

            Binder binder = this.GetEnclosingBinder(position);
            if (binder == null)
            {
                return null;
            }

            if (bindingOption == SpeculativeBindingOption.BindAsTypeOrNamespace && IsInTypeofExpression(position))
            {
                // If position is within a typeof expression, GetEnclosingBinder may return a
                // TypeofBinder.  However, this TypeofBinder will have been constructed with the
                // actual syntax of the typeof argument and we want to use the given syntax.
                // Wrap the binder in another TypeofBinder to overrule its description of where
                // unbound generic types are allowed.
                //Debug.Assert(binder is TypeofBinder); // Expectation, not requirement.
                binder = new TypeofBinder(expression, binder);
            }

            binder = new WithNullableContextBinder(SyntaxTree, position, binder);

            return new ExecutableCodeBinder(expression, binder.ContainingMemberOrLambda, binder).GetBinder(expression);
        }

        private Binder GetSpeculativeBinderForAttribute(int position, AttributeSyntax attribute)
        {
            position = CheckAndAdjustPositionForSpeculativeAttribute(position);

            var binder = this.GetEnclosingBinder(position);
            if (binder == null)
            {
                return null;
            }

            return new ExecutableCodeBinder(attribute, binder.ContainingMemberOrLambda, binder).GetBinder(attribute);
        }

        private static BoundExpression GetSpeculativelyBoundExpressionHelper(Binder binder, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            Debug.Assert(binder != null);
            Debug.Assert(binder.IsSemanticModelBinder);
            Debug.Assert(expression != null);
            Debug.Assert(bindingOption != SpeculativeBindingOption.BindAsTypeOrNamespace || expression is TypeSyntax);

            BoundExpression boundNode;
            if (bindingOption == SpeculativeBindingOption.BindAsTypeOrNamespace || binder.Flags.Includes(BinderFlags.CrefParameterOrReturnType))
            {
                boundNode = binder.BindNamespaceOrType(expression, BindingDiagnosticBag.Discarded);
            }
            else
            {
                Debug.Assert(bindingOption == SpeculativeBindingOption.BindAsExpression);
                boundNode = binder.BindExpression(expression, BindingDiagnosticBag.Discarded);
            }

            return boundNode;
        }

        /// <summary>
        /// Bind the given expression speculatively at the given position, and return back
        /// the resulting bound node. May return null in some error cases.
        /// </summary>
        /// <remarks>
        /// Keep in sync with Binder.BindCrefParameterOrReturnType.
        /// </remarks>
        protected BoundExpression GetSpeculativelyBoundExpressionWithoutNullability(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption, out Binder binder, out ImmutableArray<Symbol> crefSymbols)
        {
            if (expression == null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            crefSymbols = default(ImmutableArray<Symbol>);

            expression = SyntaxFactory.GetStandaloneExpression(expression);

            binder = this.GetSpeculativeBinder(position, expression, bindingOption);
            if (binder == null)
            {
                return null;
            }

            if (binder.Flags.Includes(BinderFlags.CrefParameterOrReturnType))
            {
                crefSymbols = ImmutableArray.Create<Symbol>(binder.BindType(expression, BindingDiagnosticBag.Discarded).Type);
                return null;
            }
            else if (binder.InCref)
            {
                if (expression.IsKind(SyntaxKind.QualifiedName))
                {
                    var qualified = (QualifiedNameSyntax)expression;
                    var crefWrapper = SyntaxFactory.QualifiedCref(qualified.Left, SyntaxFactory.NameMemberCref(qualified.Right));
                    crefSymbols = BindCref(crefWrapper, binder);
                }
                else if (expression is TypeSyntax typeSyntax)
                {
                    var crefWrapper = typeSyntax is PredefinedTypeSyntax ?
                        (CrefSyntax)SyntaxFactory.TypeCref(typeSyntax) :
                        SyntaxFactory.NameMemberCref(typeSyntax);
                    crefSymbols = BindCref(crefWrapper, binder);
                }

                return null;
            }

            var boundNode = GetSpeculativelyBoundExpressionHelper(binder, expression, bindingOption);
            return boundNode;
        }

        internal static ImmutableArray<Symbol> BindCref(CrefSyntax crefSyntax, Binder binder)
        {
            Symbol unusedAmbiguityWinner;
            var symbols = binder.BindCref(crefSyntax, out unusedAmbiguityWinner, BindingDiagnosticBag.Discarded);
            return symbols;
        }

        internal SymbolInfo GetCrefSymbolInfo(int position, CrefSyntax crefSyntax, SymbolInfoOptions options, bool hasParameterList)
        {
            var binder = this.GetEnclosingBinder(position);
            if (binder?.InCref == true)
            {
                ImmutableArray<Symbol> symbols = BindCref(crefSyntax, binder);
                return GetCrefSymbolInfo(OneOrMany.Create(symbols), options, hasParameterList);
            }

            return SymbolInfo.None;
        }

        internal static bool HasParameterList(CrefSyntax crefSyntax)
        {
            while (crefSyntax.Kind() == SyntaxKind.QualifiedCref)
            {
                crefSyntax = ((QualifiedCrefSyntax)crefSyntax).Member;
            }

            switch (crefSyntax.Kind())
            {
                case SyntaxKind.NameMemberCref:
                    return ((NameMemberCrefSyntax)crefSyntax).Parameters != null;
                case SyntaxKind.IndexerMemberCref:
                    return ((IndexerMemberCrefSyntax)crefSyntax).Parameters != null;
                case SyntaxKind.OperatorMemberCref:
                    return ((OperatorMemberCrefSyntax)crefSyntax).Parameters != null;
                case SyntaxKind.ConversionOperatorMemberCref:
                    return ((ConversionOperatorMemberCrefSyntax)crefSyntax).Parameters != null;
                case SyntaxKind.ExtensionMemberCref:
                    return HasParameterList(((ExtensionMemberCrefSyntax)crefSyntax).Member);
            }

            return false;
        }

        private static SymbolInfo GetCrefSymbolInfo(OneOrMany<Symbol> symbols, SymbolInfoOptions options, bool hasParameterList)
        {
            switch (symbols.Count)
            {
                case 0:
                    return SymbolInfo.None;
                case 1:
                    // Might have to expand an ExtendedErrorTypeSymbol into multiple candidates.
                    return GetSymbolInfoForSymbol(symbols[0], options);
                default:
                    if ((options & SymbolInfoOptions.ResolveAliases) == SymbolInfoOptions.ResolveAliases)
                    {
                        symbols = UnwrapAliases(symbols);
                    }

                    LookupResultKind resultKind = LookupResultKind.Ambiguous;

                    // The boundary between Ambiguous and OverloadResolutionFailure is less clear-cut for crefs.
                    // We'll say that overload resolution failed if the syntax has a parameter list and if
                    // all of the candidates have the same kind.
                    SymbolKind firstCandidateKind = symbols[0].Kind;
                    if (hasParameterList && symbols.All(s => s.Kind == firstCandidateKind))
                    {
                        resultKind = LookupResultKind.OverloadResolutionFailure;
                    }

                    return SymbolInfoFactory.Create(symbols, resultKind, isDynamic: false);
            }
        }

        /// <summary>
        /// Bind the given attribute speculatively at the given position, and return back
        /// the resulting bound node. May return null in some error cases.
        /// </summary>
        private BoundAttribute GetSpeculativelyBoundAttribute(int position, AttributeSyntax attribute, out Binder binder)
        {
            if (attribute == null)
            {
                throw new ArgumentNullException(nameof(attribute));
            }

            binder = this.GetSpeculativeBinderForAttribute(position, attribute);
            if (binder == null)
            {
                return null;
            }

            AliasSymbol aliasOpt; // not needed.
            NamedTypeSymbol attributeType = (NamedTypeSymbol)binder.BindType(attribute.Name, BindingDiagnosticBag.Discarded, out aliasOpt).Type;
            // note: we don't need to pass an 'attributedMember' here because we only need symbolInfo from this node
            var boundNode = new ExecutableCodeBinder(attribute, binder.ContainingMemberOrLambda, binder).BindAttribute(attribute, attributeType, attributedMember: null, BindingDiagnosticBag.Discarded);

            return boundNode;
        }

        // When speculatively binding an attribute, we have to use the name lookup rules for an attribute,
        // even if the position isn't within an attribute. For example:
        //   class C {
        //      class DAttribute: Attribute {}
        //   }
        //
        // If we speculatively bind the attribute "D" with position at the beginning of "class C", it should
        // bind to DAttribute. 
        //
        // But GetBinderForPosition won't do that; it only handles the case where position is inside an attribute.
        // This function adds a special case: if the position (after first adjustment) is at the exact beginning
        // of a type or method, the position is adjusted so the right binder is chosen to get the right things
        // in scope.
        private int CheckAndAdjustPositionForSpeculativeAttribute(int position)
        {
            position = CheckAndAdjustPosition(position);

            SyntaxToken token = Root.FindToken(position);
            if (position == 0 && position != token.SpanStart)
                return position;

            CSharpSyntaxNode node = (CSharpSyntaxNode)token.Parent;
            if (position == node.SpanStart)
            {
                // There are two cases where the binder chosen for a position at the beginning of a symbol
                // is incorrect for binding an attribute:
                //
                //   For a type, the binder should be the one that is used for the interior of the type, where
                //   the types members (and type parameters) are in scope. We adjust the position to the "{" to get
                //   that binder.
                //
                //   For a generic method, the binder should not include the type parameters. We adjust the position to
                //   the method name to get that binder.

                if (node is BaseTypeDeclarationSyntax typeDecl)
                {
                    // We're at the beginning of a type declaration. We want the members to be in scope for attributes,
                    // so use the open brace token.
                    position = typeDecl.OpenBraceToken.SpanStart;
                }

                var methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                if (methodDecl?.SpanStart == position)
                {
                    // We're at the beginning of a method declaration. We want the type parameters to NOT be in scope.
                    position = methodDecl.Identifier.SpanStart;
                }
            }

            return position;
        }

        #endregion Helpers for speculative binding

        protected override IOperation GetOperationCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            var csnode = (CSharpSyntaxNode)node;
            CheckSyntaxNode(csnode);

            return this.GetOperationWorker(csnode, cancellationToken);
        }

        internal virtual IOperation GetOperationWorker(CSharpSyntaxNode node, CancellationToken cancellationToken)
        {
            return null;
        }

        #region GetSymbolInfo

        /// <summary>
        /// Gets the semantic information for an ordering clause in an orderby query clause.
        /// </summary>
        public abstract SymbolInfo GetSymbolInfo(OrderingSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets the semantic information associated with a select or group clause.
        /// </summary>
        public abstract SymbolInfo GetSymbolInfo(SelectOrGroupClauseSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets the SymbolInfo for the Deconstruct method used for a deconstruction pattern clause, if any.
        /// </summary>
        public SymbolInfo GetSymbolInfo(PositionalPatternClauseSyntax node, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(node);
            return this.GetSymbolInfoWorker(node, SymbolInfoOptions.DefaultOptions, cancellationToken);
        }

        /// <summary>
        /// Returns what symbol(s), if any, the given expression syntax bound to in the program.
        /// 
        /// An AliasSymbol will never be returned by this method. What the alias refers to will be
        /// returned instead. To get information about aliases, call GetAliasInfo.
        /// 
        /// If binding the type name C in the expression "new C(...)" the actual constructor bound to
        /// will be returned (or all constructor if overload resolution failed). This occurs as long as C
        /// unambiguously binds to a single type that has a constructor. If C ambiguously binds to multiple
        /// types, or C binds to a static class, then type(s) are returned.
        /// </summary>
        public SymbolInfo GetSymbolInfo(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            if (!CanGetSemanticInfo(expression, allowNamedArgumentName: true))
            {
                return SymbolInfo.None;
            }
            else if (SyntaxFacts.IsNamedArgumentName(expression))
            {
                // Named arguments handled in special way.
                return this.GetNamedArgumentSymbolInfo((IdentifierNameSyntax)expression, cancellationToken);
            }
            else if (SyntaxFacts.IsDeclarationExpressionType(expression, out DeclarationExpressionSyntax parent))
            {
                switch (parent.Designation.Kind())
                {
                    case SyntaxKind.SingleVariableDesignation:
                        return GetSymbolInfoFromSymbolOrNone(TypeFromVariable((SingleVariableDesignationSyntax)parent.Designation, cancellationToken).Type);

                    case SyntaxKind.DiscardDesignation:
                        return GetSymbolInfoFromSymbolOrNone(GetTypeInfoWorker(parent, cancellationToken).Type.GetPublicSymbol());

                    case SyntaxKind.ParenthesizedVariableDesignation:
                        if (((TypeSyntax)expression).IsVar)
                        {
                            var varTypeInfo = GetTypeInfoWorker(expression, cancellationToken);
                            if (varTypeInfo.Type is { TypeKind: not TypeKind.Error })
                            {
                                return GetSymbolInfoFromSymbolOrNone(varTypeInfo.Type.GetPublicSymbol());
                            }

                            return GetSymbolInfoFromSymbolOrNone(GetTypeInfoWorker(parent, cancellationToken).Type.GetPublicSymbol());
                        }

                        break;
                }
            }
            else if (expression is DeclarationExpressionSyntax declaration)
            {
                if (declaration.Designation.Kind() != SyntaxKind.SingleVariableDesignation)
                {
                    return SymbolInfo.None;
                }

                var symbol = GetDeclaredSymbol((SingleVariableDesignationSyntax)declaration.Designation, cancellationToken);
                if ((object)symbol == null)
                {
                    return SymbolInfo.None;
                }
                return new SymbolInfo(symbol);
            }

            return this.GetSymbolInfoWorker(expression, SymbolInfoOptions.DefaultOptions, cancellationToken);
        }

        private static SymbolInfo GetSymbolInfoFromSymbolOrNone(ITypeSymbol type)
        {
            if (type?.Kind != SymbolKind.ErrorType)
            {
                return new SymbolInfo(type);
            }

            return SymbolInfo.None;
        }

        /// <summary>
        /// Given a variable designation (typically in the left-hand-side of a deconstruction declaration statement),
        /// figure out its type by looking at the declared symbol of the corresponding variable.
        /// </summary>
        private (ITypeSymbol Type, CodeAnalysis.NullableAnnotation Annotation) TypeFromVariable(SingleVariableDesignationSyntax variableDesignation, CancellationToken cancellationToken)
        {
            var variable = GetDeclaredSymbol(variableDesignation, cancellationToken);

            switch (variable)
            {
                case ILocalSymbol local:
                    return (local.Type, local.NullableAnnotation);
                case IFieldSymbol field:
                    return (field.Type, field.NullableAnnotation);
            }

            return default;
        }

        /// <summary>
        /// Returns what 'Add' method symbol(s), if any, corresponds to the given expression syntax 
        /// within <see cref="BaseObjectCreationExpressionSyntax.Initializer"/>.
        /// </summary>
        public SymbolInfo GetCollectionInitializerSymbolInfo(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            if (expression.Parent != null && expression.Parent.Kind() == SyntaxKind.CollectionInitializerExpression)
            {
                // Find containing object creation expression

                InitializerExpressionSyntax initializer = (InitializerExpressionSyntax)expression.Parent;

                // Skip containing object initializers
                while (initializer.Parent != null &&
                       initializer.Parent.Kind() == SyntaxKind.SimpleAssignmentExpression &&
                       ((AssignmentExpressionSyntax)initializer.Parent).Right == initializer &&
                       initializer.Parent.Parent != null &&
                       initializer.Parent.Parent.Kind() == SyntaxKind.ObjectInitializerExpression)
                {
                    initializer = (InitializerExpressionSyntax)initializer.Parent.Parent;
                }

                if (initializer.Parent is BaseObjectCreationExpressionSyntax objectCreation &&
                    objectCreation.Initializer == initializer &&
                    CanGetSemanticInfo(objectCreation, allowNamedArgumentName: false))
                {
                    return GetCollectionInitializerSymbolInfoWorker((InitializerExpressionSyntax)expression.Parent, expression, cancellationToken);
                }
            }

            return SymbolInfo.None;
        }

        /// <summary>
        /// Returns what symbol(s), if any, the given constructor initializer syntax bound to in the program.
        /// </summary>
        /// <param name="constructorInitializer">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public SymbolInfo GetSymbolInfo(ConstructorInitializerSyntax constructorInitializer, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(constructorInitializer);

            return CanGetSemanticInfo(constructorInitializer)
                ? GetSymbolInfoWorker(constructorInitializer, SymbolInfoOptions.DefaultOptions, cancellationToken)
                : SymbolInfo.None;
        }

        /// <summary>
        /// Returns what symbol(s), if any, the given constructor initializer syntax bound to in the program.
        /// </summary>
        /// <param name="constructorInitializer">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public SymbolInfo GetSymbolInfo(PrimaryConstructorBaseTypeSyntax constructorInitializer, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(constructorInitializer);

            return CanGetSemanticInfo(constructorInitializer)
                ? GetSymbolInfoWorker(constructorInitializer, SymbolInfoOptions.DefaultOptions, cancellationToken)
                : SymbolInfo.None;
        }

        /// <summary>
        /// Returns what symbol(s), if any, the given attribute syntax bound to in the program.
        /// </summary>
        /// <param name="attributeSyntax">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public SymbolInfo GetSymbolInfo(AttributeSyntax attributeSyntax, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(attributeSyntax);

            return CanGetSemanticInfo(attributeSyntax)
                ? GetSymbolInfoWorker(attributeSyntax, SymbolInfoOptions.DefaultOptions, cancellationToken)
                : SymbolInfo.None;
        }

        /// <summary>
        /// Gets the semantic information associated with a documentation comment cref.
        /// </summary>
        public SymbolInfo GetSymbolInfo(CrefSyntax crefSyntax, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(crefSyntax);

            return CanGetSemanticInfo(crefSyntax)
                ? GetSymbolInfoWorker(crefSyntax, SymbolInfoOptions.DefaultOptions, cancellationToken)
                : SymbolInfo.None;
        }

        /// <summary>
        /// Binds the expression in the context of the specified location and gets symbol information.
        /// This method is used to get symbol information about an expression that did not actually
        /// appear in the source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and
        /// accessibility. This character position must be within the FullSpan of the Root syntax
        /// node in this SemanticModel.
        /// </param>
        /// <param name="expression">A syntax node that represents a parsed expression. This syntax
        /// node need not and typically does not appear in the source code referred to by the
        /// SemanticModel instance.</param>
        /// <param name="bindingOption">Indicates whether to binding the expression as a full expressions,
        /// or as a type or namespace. If SpeculativeBindingOption.BindAsTypeOrNamespace is supplied, then
        /// expression should derive from TypeSyntax.</param>
        /// <returns>The symbol information for the topmost node of the expression.</returns>
        /// <remarks>
        /// The passed in expression is interpreted as a stand-alone expression, as if it
        /// appeared by itself somewhere within the scope that encloses "position".
        /// 
        /// <paramref name="bindingOption"/> is ignored if <paramref name="position"/> is within a documentation
        /// comment cref attribute value.
        /// </remarks>
        public SymbolInfo GetSpeculativeSymbolInfo(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            if (!CanGetSemanticInfo(expression, isSpeculative: true)) return SymbolInfo.None;

            Binder binder;
            ImmutableArray<Symbol> crefSymbols;
            BoundNode boundNode = GetSpeculativelyBoundExpression(position, expression, bindingOption, out binder, out crefSymbols); //calls CheckAndAdjustPosition
            Debug.Assert(boundNode == null || crefSymbols.IsDefault);
            if (boundNode == null)
            {
                return crefSymbols.IsDefault ? SymbolInfo.None : GetCrefSymbolInfo(OneOrMany.Create(crefSymbols), SymbolInfoOptions.DefaultOptions, hasParameterList: false);
            }

            var symbolInfo = this.GetSymbolInfoForNode(SymbolInfoOptions.DefaultOptions, boundNode, boundNode, boundNodeForSyntacticParent: null, binderOpt: binder);

            return symbolInfo;
        }

        /// <summary>
        /// Bind the attribute in the context of the specified location and get semantic information
        /// such as type, symbols and diagnostics. This method is used to get semantic information about an attribute
        /// that did not actually appear in the source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel. In order to obtain
        /// the correct scoping rules for the attribute, position should be the Start position of the Span of the symbol that
        /// the attribute is being applied to.
        /// </param>
        /// <param name="attribute">A syntax node that represents a parsed attribute. This syntax node
        /// need not and typically does not appear in the source code referred to SemanticModel instance.</param>
        /// <returns>The semantic information for the topmost node of the attribute.</returns>
        public SymbolInfo GetSpeculativeSymbolInfo(int position, AttributeSyntax attribute)
        {
            Debug.Assert(CanGetSemanticInfo(attribute, isSpeculative: true));

            Binder binder;
            BoundNode boundNode = GetSpeculativelyBoundAttribute(position, attribute, out binder); //calls CheckAndAdjustPosition
            if (boundNode == null)
                return SymbolInfo.None;

            var symbolInfo = this.GetSymbolInfoForNode(SymbolInfoOptions.DefaultOptions, boundNode, boundNode, boundNodeForSyntacticParent: null, binderOpt: binder);

            return symbolInfo;
        }

        /// <summary>
        /// Bind the constructor initializer in the context of the specified location and get semantic information
        /// such as type, symbols and diagnostics. This method is used to get semantic information about a constructor
        /// initializer that did not actually appear in the source code.
        /// 
        /// NOTE: This will only work in locations where there is already a constructor initializer.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// Furthermore, it must be within the span of an existing constructor initializer.
        /// </param>
        /// <param name="constructorInitializer">A syntax node that represents a parsed constructor initializer. This syntax node
        /// need not and typically does not appear in the source code referred to SemanticModel instance.</param>
        /// <returns>The semantic information for the topmost node of the constructor initializer.</returns>
        public SymbolInfo GetSpeculativeSymbolInfo(int position, ConstructorInitializerSyntax constructorInitializer)
        {
            Debug.Assert(CanGetSemanticInfo(constructorInitializer, isSpeculative: true));

            position = CheckAndAdjustPosition(position);

            if (constructorInitializer == null)
            {
                throw new ArgumentNullException(nameof(constructorInitializer));
            }

            // NOTE: since we're going to be depending on a MemberModel to do the binding for us,
            // we need to find a constructor initializer in the tree of this semantic model.
            // NOTE: This approach will not allow speculative binding of a constructor initializer
            // on a constructor that didn't formerly have one.
            // TODO: Should we support positions that are not in existing constructor initializers?
            // If so, we will need to build up the context that would otherwise be built up by
            // InitializerMemberModel.
            var existingConstructorInitializer = this.Root.FindToken(position).Parent.AncestorsAndSelf().OfType<ConstructorInitializerSyntax>().FirstOrDefault();

            if (existingConstructorInitializer == null)
            {
                return SymbolInfo.None;
            }

            MemberSemanticModel memberModel = GetMemberModel(existingConstructorInitializer);

            if (memberModel == null)
            {
                return SymbolInfo.None;
            }

            var binder = memberModel.GetEnclosingBinder(position);
            if (binder != null)
            {
                binder = new ExecutableCodeBinder(constructorInitializer, binder.ContainingMemberOrLambda, binder);

                BoundExpressionStatement bnode = binder.BindConstructorInitializer(constructorInitializer, BindingDiagnosticBag.Discarded);
                var binfo = GetSymbolInfoFromBoundConstructorInitializer(memberModel, binder, bnode);
                return binfo;
            }
            else
            {
                return SymbolInfo.None;
            }
        }

        private static SymbolInfo GetSymbolInfoFromBoundConstructorInitializer(MemberSemanticModel memberModel, Binder binder, BoundExpressionStatement bnode)
        {
            BoundExpression expression = bnode.Expression;

            while (expression is BoundSequence sequence)
            {
                expression = sequence.Value;
            }

            return memberModel.GetSymbolInfoForNode(SymbolInfoOptions.DefaultOptions, expression, expression, boundNodeForSyntacticParent: null, binderOpt: binder);
        }

        /// <summary>
        /// Bind the constructor initializer in the context of the specified location and get semantic information
        /// about symbols. This method is used to get semantic information about a constructor
        /// initializer that did not actually appear in the source code.
        /// 
        /// NOTE: This will only work in locations where there is already a constructor initializer.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the span of an existing constructor initializer.
        /// </param>
        /// <param name="constructorInitializer">A syntax node that represents a parsed constructor initializer. This syntax node
        /// need not and typically does not appear in the source code referred to SemanticModel instance.</param>
        /// <returns>The semantic information for the topmost node of the constructor initializer.</returns>
        public SymbolInfo GetSpeculativeSymbolInfo(int position, PrimaryConstructorBaseTypeSyntax constructorInitializer)
        {
            Debug.Assert(CanGetSemanticInfo(constructorInitializer, isSpeculative: true));

            position = CheckAndAdjustPosition(position);

            if (constructorInitializer == null)
            {
                throw new ArgumentNullException(nameof(constructorInitializer));
            }

            // NOTE: since we're going to be depending on a MemberModel to do the binding for us,
            // we need to find a constructor initializer in the tree of this semantic model.
            // NOTE: This approach will not allow speculative binding of a constructor initializer
            // on a constructor that didn't formerly have one.
            // TODO: Should we support positions that are not in existing constructor initializers?
            // If so, we will need to build up the context that would otherwise be built up by
            // InitializerMemberModel.
            var existingConstructorInitializer = this.Root.FindToken(position).Parent.AncestorsAndSelf().OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault();

            if (existingConstructorInitializer == null)
            {
                return SymbolInfo.None;
            }

            MemberSemanticModel memberModel = GetMemberModel(existingConstructorInitializer);

            if (memberModel == null)
            {
                return SymbolInfo.None;
            }

            var argumentList = existingConstructorInitializer.ArgumentList;
            var binder = memberModel.GetEnclosingBinder(LookupPosition.IsBetweenTokens(position, argumentList.OpenParenToken, argumentList.CloseParenToken) ? position : argumentList.OpenParenToken.SpanStart);
            if (binder != null)
            {
                binder = new ExecutableCodeBinder(constructorInitializer, binder.ContainingMemberOrLambda, binder);

                BoundExpressionStatement bnode = binder.BindConstructorInitializer(constructorInitializer, BindingDiagnosticBag.Discarded);
                SymbolInfo binfo = GetSymbolInfoFromBoundConstructorInitializer(memberModel, binder, bnode);
                return binfo;
            }
            else
            {
                return SymbolInfo.None;
            }
        }

        /// <summary>
        /// Bind the cref in the context of the specified location and get semantic information
        /// such as type, symbols and diagnostics. This method is used to get semantic information about a cref
        /// that did not actually appear in the source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel. In order to obtain
        /// the correct scoping rules for the cref, position should be the Start position of the Span of the original cref.
        /// </param>
        /// <param name="cref">A syntax node that represents a parsed cref. This syntax node
        /// need not and typically does not appear in the source code referred to SemanticModel instance.</param>
        /// <param name="options">SymbolInfo options.</param>
        /// <returns>The semantic information for the topmost node of the cref.</returns>
        public SymbolInfo GetSpeculativeSymbolInfo(int position, CrefSyntax cref, SymbolInfoOptions options = SymbolInfoOptions.DefaultOptions)
        {
            Debug.Assert(CanGetSemanticInfo(cref, isSpeculative: true));

            position = CheckAndAdjustPosition(position);
            return this.GetCrefSymbolInfo(position, cref, options, HasParameterList(cref));
        }

        #endregion GetSymbolInfo

        #region GetTypeInfo

        /// <summary>
        /// Gets type information about a constructor initializer.
        /// </summary>
        /// <param name="constructorInitializer">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public TypeInfo GetTypeInfo(ConstructorInitializerSyntax constructorInitializer, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(constructorInitializer);

            return CanGetSemanticInfo(constructorInitializer)
                ? GetTypeInfoWorker(constructorInitializer, cancellationToken)
                : CSharpTypeInfo.None;
        }

        public abstract TypeInfo GetTypeInfo(SelectOrGroupClauseSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        public TypeInfo GetTypeInfo(PatternSyntax pattern, CancellationToken cancellationToken = default(CancellationToken))
        {
            while (pattern is ParenthesizedPatternSyntax pp)
                pattern = pp.Pattern;

            CheckSyntaxNode(pattern);
            return GetTypeInfoWorker(pattern, cancellationToken);
        }

        /// <summary>
        /// Gets type information about an expression.
        /// </summary>
        /// <param name="expression">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public TypeInfo GetTypeInfo(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            if (!CanGetSemanticInfo(expression))
            {
                return CSharpTypeInfo.None;
            }
            else if (SyntaxFacts.IsDeclarationExpressionType(expression, out DeclarationExpressionSyntax parent))
            {
                switch (parent.Designation.Kind())
                {
                    case SyntaxKind.SingleVariableDesignation:
                        var (declarationType, annotation) = ((ITypeSymbol, CodeAnalysis.NullableAnnotation))TypeFromVariable((SingleVariableDesignationSyntax)parent.Designation, cancellationToken);
                        var declarationTypeSymbol = declarationType.GetSymbol();
                        var nullabilityInfo = annotation.ToNullabilityInfo(declarationTypeSymbol);
                        return new CSharpTypeInfo(declarationTypeSymbol, declarationTypeSymbol, nullabilityInfo, nullabilityInfo, Conversion.Identity);

                    case SyntaxKind.DiscardDesignation:
                        var declarationInfo = GetTypeInfoWorker(parent, cancellationToken);
                        return new CSharpTypeInfo(declarationInfo.Type, declarationInfo.Type, declarationInfo.Nullability, declarationInfo.Nullability, Conversion.Identity);

                    case SyntaxKind.ParenthesizedVariableDesignation:
                        if (((TypeSyntax)expression).IsVar)
                        {
                            var varTypeInfo = GetTypeInfoWorker(expression, cancellationToken);
                            if (varTypeInfo.Type is { TypeKind: not TypeKind.Error })
                            {
                                return varTypeInfo;
                            }

                            return GetTypeInfoWorker(parent, cancellationToken);
                        }

                        break;
                }
            }

            return GetTypeInfoWorker(expression, cancellationToken);
        }

        /// <summary>
        /// Gets type information about an attribute.
        /// </summary>
        /// <param name="attributeSyntax">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public TypeInfo GetTypeInfo(AttributeSyntax attributeSyntax, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(attributeSyntax);

            return CanGetSemanticInfo(attributeSyntax)
                ? GetTypeInfoWorker(attributeSyntax, cancellationToken)
                : CSharpTypeInfo.None;
        }

        /// <summary>
        /// Gets the conversion that occurred between the expression's type and type implied by the expression's context.
        /// </summary>
        public Conversion GetConversion(SyntaxNode expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            var csnode = (CSharpSyntaxNode)expression;

            CheckSyntaxNode(csnode);

            var info = CanGetSemanticInfo(csnode)
                ? GetTypeInfoWorker(csnode, cancellationToken)
                : CSharpTypeInfo.None;

            return info.ImplicitConversion;
        }

        /// <summary>
        /// Binds the expression in the context of the specified location and gets type information.
        /// This method is used to get type information about an expression that did not actually
        /// appear in the source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and
        /// accessibility. This character position must be within the FullSpan of the Root syntax
        /// node in this SemanticModel.
        /// </param>
        /// <param name="expression">A syntax node that represents a parsed expression. This syntax
        /// node need not and typically does not appear in the source code referred to by the
        /// SemanticModel instance.</param>
        /// <param name="bindingOption">Indicates whether to binding the expression as a full expressions,
        /// or as a type or namespace. If SpeculativeBindingOption.BindAsTypeOrNamespace is supplied, then
        /// expression should derive from TypeSyntax.</param>
        /// <returns>The type information for the topmost node of the expression.</returns>
        /// <remarks>The passed in expression is interpreted as a stand-alone expression, as if it
        /// appeared by itself somewhere within the scope that encloses "position".</remarks>
        public TypeInfo GetSpeculativeTypeInfo(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            return GetSpeculativeTypeInfoWorker(position, expression, bindingOption);
        }

        internal CSharpTypeInfo GetSpeculativeTypeInfoWorker(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            if (!CanGetSemanticInfo(expression, isSpeculative: true))
            {
                return CSharpTypeInfo.None;
            }

            ImmutableArray<Symbol> crefSymbols;
            BoundNode boundNode = GetSpeculativelyBoundExpression(position, expression, bindingOption, out _, out crefSymbols); //calls CheckAndAdjustPosition
            Debug.Assert(boundNode == null || crefSymbols.IsDefault);
            if (boundNode == null)
            {
                return !crefSymbols.IsDefault && crefSymbols.Length == 1
                    ? GetTypeInfoForSymbol(crefSymbols[0])
                    : CSharpTypeInfo.None;
            }

            var typeInfo = GetTypeInfoForNode(boundNode, boundNode, boundNodeForSyntacticParent: null);

            return typeInfo;
        }

        /// <summary>
        /// Gets the conversion that occurred between the expression's type and type implied by the expression's context.
        /// </summary>
        public Conversion GetSpeculativeConversion(int position, ExpressionSyntax expression, SpeculativeBindingOption bindingOption)
        {
            var info = this.GetSpeculativeTypeInfoWorker(position, expression, bindingOption);
            return info.ImplicitConversion;
        }

        #endregion GetTypeInfo

        #region GetMemberGroup

        /// <summary>
        /// Gets a list of method or indexed property symbols for a syntax node.
        /// </summary>
        /// <param name="expression">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public ImmutableArray<ISymbol> GetMemberGroup(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            return CanGetSemanticInfo(expression)
                ? this.GetMemberGroupWorker(expression, SymbolInfoOptions.DefaultOptions, cancellationToken).GetPublicSymbols()
                : ImmutableArray<ISymbol>.Empty;
        }

        /// <summary>
        /// Gets a list of method or indexed property symbols for a syntax node.
        /// </summary>
        /// <param name="attribute">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public ImmutableArray<ISymbol> GetMemberGroup(AttributeSyntax attribute, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(attribute);

            return CanGetSemanticInfo(attribute)
                ? this.GetMemberGroupWorker(attribute, SymbolInfoOptions.DefaultOptions, cancellationToken).GetPublicSymbols()
                : ImmutableArray<ISymbol>.Empty;
        }

        /// <summary>
        /// Gets a list of method symbols for a syntax node.
        /// </summary>
        /// <param name="initializer">The syntax node to get semantic information for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public ImmutableArray<ISymbol> GetMemberGroup(ConstructorInitializerSyntax initializer, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(initializer);

            return CanGetSemanticInfo(initializer)
                ? this.GetMemberGroupWorker(initializer, SymbolInfoOptions.DefaultOptions, cancellationToken).GetPublicSymbols()
                : ImmutableArray<ISymbol>.Empty;
        }

        #endregion GetMemberGroup

        #region GetIndexerGroup

        /// <summary>
        /// Returns the list of accessible, non-hidden indexers that could be invoked with the given expression as receiver.
        /// </summary>
        /// <param name="expression">Potential indexer receiver.</param>
        /// <param name="cancellationToken">To cancel the computation.</param>
        /// <returns>Accessible, non-hidden indexers.</returns>
        /// <remarks>
        /// If the receiver is an indexer expression, the list will contain the indexers that could be applied to the result
        /// of accessing the indexer, not the set of candidates that were considered during construction of the indexer expression.
        /// </remarks>
        public ImmutableArray<IPropertySymbol> GetIndexerGroup(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            return CanGetSemanticInfo(expression)
                ? this.GetIndexerGroupWorker(expression, SymbolInfoOptions.DefaultOptions, cancellationToken)
                : ImmutableArray<IPropertySymbol>.Empty;
        }

        #endregion GetIndexerGroup

        #region GetConstantValue

        public Optional<object> GetConstantValue(ExpressionSyntax expression, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(expression);

            return CanGetSemanticInfo(expression)
                ? this.GetConstantValueWorker(expression, cancellationToken)
                : default(Optional<object>);
        }

        #endregion GetConstantValue

        /// <summary>
        /// Gets the semantic information associated with a query clause.
        /// </summary>
        public abstract QueryClauseInfo GetQueryClauseInfo(QueryClauseSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// If <paramref name="nameSyntax"/> resolves to an alias name, return the AliasSymbol corresponding
        /// to A. Otherwise return null.
        /// </summary>
        public IAliasSymbol GetAliasInfo(IdentifierNameSyntax nameSyntax, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(nameSyntax);

            if (!CanGetSemanticInfo(nameSyntax))
                return null;

            SymbolInfo info = GetSymbolInfoWorker(nameSyntax, SymbolInfoOptions.PreferTypeToConstructors | SymbolInfoOptions.PreserveAliases, cancellationToken);
            return info.Symbol as IAliasSymbol;
        }

        /// <summary>
        /// Binds the name in the context of the specified location and sees if it resolves to an
        /// alias name. If it does, return the AliasSymbol corresponding to it. Otherwise, return null.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and
        /// accessibility. This character position must be within the FullSpan of the Root syntax
        /// node in this SemanticModel.
        /// </param>
        /// <param name="nameSyntax">A syntax node that represents a name. This syntax
        /// node need not and typically does not appear in the source code referred to by the
        /// SemanticModel instance.</param>
        /// <param name="bindingOption">Indicates whether to binding the name as a full expression,
        /// or as a type or namespace. If SpeculativeBindingOption.BindAsTypeOrNamespace is supplied, then
        /// expression should derive from TypeSyntax.</param>
        /// <remarks>The passed in name is interpreted as a stand-alone name, as if it
        /// appeared by itself somewhere within the scope that encloses "position".</remarks>
        public IAliasSymbol GetSpeculativeAliasInfo(int position, IdentifierNameSyntax nameSyntax, SpeculativeBindingOption bindingOption)
        {
            Binder binder;
            ImmutableArray<Symbol> crefSymbols;
            BoundNode boundNode = GetSpeculativelyBoundExpression(position, nameSyntax, bindingOption, out binder, out crefSymbols); //calls CheckAndAdjustPosition
            Debug.Assert(boundNode == null || crefSymbols.IsDefault);
            if (boundNode == null)
            {
                return !crefSymbols.IsDefault && crefSymbols.Length == 1
                    ? (crefSymbols[0] as AliasSymbol).GetPublicSymbol()
                    : null;
            }

            var symbolInfo = this.GetSymbolInfoForNode(SymbolInfoOptions.PreferTypeToConstructors | SymbolInfoOptions.PreserveAliases,
                boundNode, boundNode, boundNodeForSyntacticParent: null, binderOpt: binder);

            return symbolInfo.Symbol as IAliasSymbol;
        }

        /// <summary>
        /// Gets the binder that encloses the position.
        /// </summary>
        internal Binder GetEnclosingBinder(int position)
        {
            Binder result = GetEnclosingBinderInternal(position);
            Debug.Assert(result == null || result.IsSemanticModelBinder);
            return result;
        }

        internal abstract Binder GetEnclosingBinderInternal(int position);

        /// <summary>
        /// Gets the MemberSemanticModel that contains the node.
        /// </summary>
        internal abstract MemberSemanticModel GetMemberModel(SyntaxNode node);

        internal bool IsInTree(SyntaxNode node)
        {
            return node.SyntaxTree == this.SyntaxTree;
        }

        private static bool IsInStructuredTriviaOtherThanCrefOrNameAttribute(CSharpSyntaxNode node)
        {
            while (node != null)
            {
                if (node.Kind() == SyntaxKind.XmlCrefAttribute || node.Kind() == SyntaxKind.XmlNameAttribute)
                {
                    return false;
                }
                else if (node.IsStructuredTrivia)
                {
                    return true;
                }
                else
                {
                    node = node.ParentOrStructuredTriviaParent;
                }
            }
            return false;
        }

        /// <summary>
        /// Given a position, locates the containing token.  If the position is actually within the
        /// leading trivia of the containing token or if that token is EOF, moves one token to the
        /// left.  Returns the start position of the resulting token.
        /// 
        /// This has the effect of moving the position left until it hits the beginning of a non-EOF
        /// token.
        /// 
        /// Throws an ArgumentOutOfRangeException if position is not within the root of this model.
        /// </summary>
        protected int CheckAndAdjustPosition(int position)
        {
            SyntaxToken unused;
            return CheckAndAdjustPosition(position, out unused);
        }

        protected int CheckAndAdjustPosition(int position, out SyntaxToken token)
        {
            int fullStart = this.Root.Position;
            int fullEnd = this.Root.FullSpan.End;
            bool atEOF = position == fullEnd && position == this.SyntaxTree.GetRoot().FullSpan.End;

            if ((fullStart <= position && position < fullEnd) || atEOF) // allow for EOF
            {
                token = (atEOF ? (CSharpSyntaxNode)this.SyntaxTree.GetRoot() : Root).FindTokenIncludingCrefAndNameAttributes(position);

                if (position < token.SpanStart) // NB: Span, not FullSpan
                {
                    // If this is already the first token, then the result will be default(SyntaxToken)
                    token = token.GetPreviousToken();
                }

                // If the first token in the root is missing, it's possible to step backwards
                // past the start of the root.  All sorts of bad things will happen in that case,
                // so just use the start of the root span.
                // CONSIDER: this should only happen when we step past the first token found, so
                // the start of that token would be another possible return value.
                return Math.Max(token.SpanStart, fullStart);
            }
            else if (fullStart == fullEnd && position == fullEnd)
            {
                // The root is an empty span and isn't the full compilation unit. No other choice here.
                token = default(SyntaxToken);
                return fullStart;
            }

            throw new ArgumentOutOfRangeException(nameof(position), position,
                string.Format(CSharpResources.PositionIsNotWithinSyntax, Root.FullSpan));
        }

        /// <summary>
        /// A convenience method that determines a position from a node.  If the node is missing,
        /// then its position will be adjusted using CheckAndAdjustPosition.
        /// </summary>
        protected int GetAdjustedNodePosition(SyntaxNode node)
        {
            Debug.Assert(IsInTree(node));

            var fullSpan = this.Root.FullSpan;
            var position = node.SpanStart;

            // skip zero-width tokens to get the position, but never get past the end of the node
            SyntaxToken firstToken = node.GetFirstToken(includeZeroWidth: false);
            if (firstToken.Node is object)
            {
                int betterPosition = firstToken.SpanStart;
                if (betterPosition < node.Span.End)
                {
                    position = betterPosition;
                }
            }

            if (fullSpan.IsEmpty)
            {
                Debug.Assert(position == fullSpan.Start);
                // At end of zero-width full span. No need to call
                // CheckAndAdjustPosition since that will simply 
                // return the original position.
                return position;
            }
            else if (position == fullSpan.End)
            {
                Debug.Assert(node.Width == 0);
                // For zero-width node at the end of the full span,
                // check and adjust the preceding position.
                return CheckAndAdjustPosition(position - 1);
            }
            else if (node.IsMissing || node.HasErrors || node.Width == 0 || node.IsPartOfStructuredTrivia())
            {
                return CheckAndAdjustPosition(position);
            }
            else
            {
                // No need to adjust position.
                return position;
            }
        }

        [Conditional("DEBUG")]
        protected void AssertPositionAdjusted(int position)
        {
            Debug.Assert(position == CheckAndAdjustPosition(position), "Expected adjusted position");
        }

        protected void CheckSyntaxNode(CSharpSyntaxNode syntax)
        {
            if (syntax == null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            if (!IsInTree(syntax))
            {
                throw new ArgumentException(CSharpResources.SyntaxNodeIsNotWithinSynt);
            }
        }

        // This method ensures that the given syntax node to speculate is non-null and doesn't belong to a SyntaxTree of any model in the chain.
        private void CheckModelAndSyntaxNodeToSpeculate(CSharpSyntaxNode syntax)
        {
            if (syntax == null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            if (this.IsSpeculativeSemanticModel)
            {
                throw new InvalidOperationException(CSharpResources.ChainingSpeculativeModelIsNotSupported);
            }

            if (this.Compilation.ContainsSyntaxTree(syntax.SyntaxTree))
            {
                throw new ArgumentException(CSharpResources.SpeculatedSyntaxNodeCannotBelongToCurrentCompilation);
            }
        }

        /// <summary>
        /// Gets the available named symbols in the context of the specified location and optional container. Only
        /// symbols that are accessible and visible from the given location are returned.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration scope and
        /// accessibility.</param>
        /// <param name="container">The container to search for symbols within. If null then the enclosing declaration
        /// scope around position is used.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <param name="includeExtensions">Consider extension members. Classic extension methods will be returned in reduced form.</param>
        /// <returns>A list of symbols that were found. If no symbols were found, an empty list is returned.</returns>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible. Even if "container" is
        /// specified, the "position" location is significant for determining which members of "containing" are
        /// accessible. 
        /// 
        /// Labels are not considered (see <see cref="LookupLabels"/>).
        /// 
        /// Non-reduced extension methods are considered regardless of the value of <paramref name="includeExtensions"/>.
        /// </remarks>
        public ImmutableArray<ISymbol> LookupSymbols(
            int position,
            NamespaceOrTypeSymbol container = null,
            string name = null,
            bool includeExtensions = false)
        {
            var options = includeExtensions ? LookupOptions.IncludeExtensionMembers : LookupOptions.Default;
            return LookupSymbolsInternal(position, container, name, options, useBaseReferenceAccessibility: false);
        }

        /// <summary>
        /// Gets the available base type members in the context of the specified location.  Akin to
        /// calling <see cref="LookupSymbols"/> with the container set to the immediate base type of
        /// the type in which <paramref name="position"/> occurs.  However, the accessibility rules
        /// are different: protected members of the base type will be visible.
        /// 
        /// Consider the following example:
        /// 
        ///   public class Base
        ///   {
        ///       protected void M() { }
        ///   }
        ///   
        ///   public class Derived : Base
        ///   {
        ///       void Test(Base b)
        ///       {
        ///           b.M(); // Error - cannot access protected member.
        ///           base.M();
        ///       }
        ///   }
        /// 
        /// Protected members of an instance of another type are only accessible if the instance is known
        /// to be "this" instance (as indicated by the "base" keyword).
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration scope and
        /// accessibility.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <returns>A list of symbols that were found. If no symbols were found, an empty list is returned.</returns>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible.
        /// 
        /// Non-reduced extension methods are considered, but reduced extension methods are not.
        /// </remarks>
        public new ImmutableArray<ISymbol> LookupBaseMembers(
            int position,
            string name = null)
        {
            return LookupSymbolsInternal(position, container: null, name: name, options: LookupOptions.Default, useBaseReferenceAccessibility: true);
        }

        /// <summary>
        /// Gets the available named static member symbols in the context of the specified location and optional container.
        /// Only members that are accessible and visible from the given location are returned.
        /// 
        /// Non-reduced extension methods are considered, since they are static methods.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration scope and
        /// accessibility.</param>
        /// <param name="container">The container to search for symbols within. If null then the enclosing declaration
        /// scope around position is used.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <returns>A list of symbols that were found. If no symbols were found, an empty list is returned.</returns>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible. Even if "container" is
        /// specified, the "position" location is significant for determining which members of "containing" are
        /// accessible. 
        /// </remarks>
        public ImmutableArray<ISymbol> LookupStaticMembers(
            int position,
            NamespaceOrTypeSymbol container = null,
            string name = null)
        {
            return LookupSymbolsInternal(position, container, name, LookupOptions.MustNotBeInstance, useBaseReferenceAccessibility: false);
        }

        /// <summary>
        /// Gets the available named namespace and type symbols in the context of the specified location and optional container.
        /// Only members that are accessible and visible from the given location are returned.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration scope and
        /// accessibility.</param>
        /// <param name="container">The container to search for symbols within. If null then the enclosing declaration
        /// scope around position is used.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <returns>A list of symbols that were found. If no symbols were found, an empty list is returned.</returns>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible. Even if "container" is
        /// specified, the "position" location is significant for determining which members of "containing" are
        /// accessible. 
        /// 
        /// Does not return NamespaceOrTypeSymbol, because there could be aliases.
        /// </remarks>
        public ImmutableArray<ISymbol> LookupNamespacesAndTypes(
            int position,
            NamespaceOrTypeSymbol container = null,
            string name = null)
        {
            return LookupSymbolsInternal(position, container, name, LookupOptions.NamespacesOrTypesOnly, useBaseReferenceAccessibility: false);
        }

        /// <summary>
        /// Gets the available named label symbols in the context of the specified location and optional container.
        /// Only members that are accessible and visible from the given location are returned.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration scope and
        /// accessibility.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <returns>A list of symbols that were found. If no symbols were found, an empty list is returned.</returns>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible. Even if "container" is
        /// specified, the "position" location is significant for determining which members of "containing" are
        /// accessible. 
        /// </remarks>
        public new ImmutableArray<ISymbol> LookupLabels(
            int position,
            string name = null)
        {
            return LookupSymbolsInternal(position, container: null, name: name, options: LookupOptions.LabelsOnly, useBaseReferenceAccessibility: false);
        }

        /// <summary>
        /// Gets the available named symbols in the context of the specified location and optional
        /// container. Only symbols that are accessible and visible from the given location are
        /// returned.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration
        /// scope and accessibility.</param>
        /// <param name="container">The container to search for symbols within. If null then the
        /// enclosing declaration scope around position is used.</param>
        /// <param name="name">The name of the symbol to find. If null is specified then symbols
        /// with any names are returned.</param>
        /// <param name="options">Additional options that affect the lookup process.</param>
        /// <param name="useBaseReferenceAccessibility">Ignore 'throughType' in accessibility checking. 
        /// Used in checking accessibility of symbols accessed via 'MyBase' or 'base'.</param>
        /// <remarks>
        /// The "position" is used to determine what variables are visible and accessible. Even if
        /// "container" is specified, the "position" location is significant for determining which
        /// members of "containing" are accessible. 
        /// </remarks>
        /// <exception cref="ArgumentException">Throws an argument exception if the passed lookup options are invalid.</exception>
        private ImmutableArray<ISymbol> LookupSymbolsInternal(
            int position,
            NamespaceOrTypeSymbol container,
            string name,
            LookupOptions options,
            bool useBaseReferenceAccessibility)
        {
            Debug.Assert((options & LookupOptions.UseBaseReferenceAccessibility) == 0, "Use the useBaseReferenceAccessibility parameter.");
            if (useBaseReferenceAccessibility)
            {
                options |= LookupOptions.UseBaseReferenceAccessibility;
            }
            Debug.Assert(!options.IsAttributeTypeLookup()); // Not exposed publicly.

            options.ThrowIfInvalid();

            SyntaxToken token;
            position = CheckAndAdjustPosition(position, out token);

            if ((object)container == null || container.Kind == SymbolKind.Namespace)
            {
                options &= ~LookupOptions.IncludeExtensionMembers;
            }

            var binder = GetEnclosingBinder(position);
            if (binder == null)
            {
                return ImmutableArray<ISymbol>.Empty;
            }

            if (useBaseReferenceAccessibility)
            {
                Debug.Assert((object)container == null);
                TypeSymbol containingType = binder.ContainingType;
                TypeSymbol baseType = null;

                // For a script class or a submission class base should have no members.
                if ((object)containingType != null && containingType.Kind == SymbolKind.NamedType && ((NamedTypeSymbol)containingType).IsScriptClass)
                {
                    return ImmutableArray<ISymbol>.Empty;
                }

                if ((object)containingType == null || (object)(baseType = containingType.BaseTypeNoUseSiteDiagnostics) == null)
                {
                    throw new ArgumentException(
                        "Not a valid position for a call to LookupBaseMembers (must be in a type with a base type)",
                        nameof(position));
                }
                container = baseType;
            }

            if (!binder.IsInMethodBody &&
                (options & (LookupOptions.NamespaceAliasesOnly | LookupOptions.NamespacesOrTypesOnly | LookupOptions.LabelsOnly)) == 0)
            {
                // Method type parameters are not in scope outside a method
                // body unless the position is either:
                // a) in a type-only context inside an expression, or
                // b) inside of an XML name attribute in an XML doc comment,
                // c) inside a nameof context.
                var parentExpr = token.Parent as ExpressionSyntax;
                if (parentExpr != null && !(parentExpr.Parent is XmlNameAttributeSyntax) && !SyntaxFacts.IsInTypeOnlyContext(parentExpr) && !binder.IsInsideNameof)
                {
                    options |= LookupOptions.MustNotBeMethodTypeParameter;
                }
            }

            var info = LookupSymbolsInfo.GetInstance();
            info.FilterName = name;

            if ((object)container == null)
            {
                binder.AddLookupSymbolsInfo(info, options);
            }
            else
            {
                binder.AddMemberLookupSymbolsInfo(info, container, options, binder);
            }

            var results = ArrayBuilder<ISymbol>.GetInstance(info.Count);

            if (name == null)
            {
                // If they didn't provide a name, then look up all names and associated arities 
                // and find all the corresponding symbols.
                foreach (string foundName in info.Names)
                {
                    AppendSymbolsWithName(results, foundName, binder, container, options, info);
                }
            }
            else
            {
                // They provided a name.  Find all the arities for that name, and then look all of those up.
                AppendSymbolsWithName(results, name, binder, container, options, info);
            }

            info.Free();

            if ((options & LookupOptions.IncludeExtensionMembers) != 0 && container is TypeSymbol receiverType)
            {
                var lookupResult = LookupResult.GetInstance();

                options |= LookupOptions.AllMethodsOnArityZero;
                options &= ~LookupOptions.MustBeInstance;

                binder.LookupAllExtensions(lookupResult, name, options);

                if (lookupResult.IsMultiViable)
                {
                    foreach (Symbol symbol in lookupResult.Symbols)
                    {
                        if (symbol is MethodSymbol { IsExtensionMethod: true } extensionMethod)
                        {
                            if (extensionMethod.ReduceExtensionMethod(receiverType, Compilation) is { } reduced)
                            {
                                results.Add(reduced.GetPublicSymbol());
                            }
                        }
                        else
                        {
                            Debug.Assert(symbol.GetIsNewExtensionMember());
                            if (SourceNamedTypeSymbol.GetCompatibleSubstitutedMember(binder.Compilation, symbol, receiverType) is { } compatibleSubstitutedMember)
                            {
                                results.Add(compatibleSubstitutedMember.GetPublicSymbol());
                            }
                        }
                    }
                }

                lookupResult.Free();
            }

            if (name == null)
                results.RemoveAll(static (symbol, _) => !symbol.CanBeReferencedByName, arg: 0);

            return results.ToImmutableAndFree();
        }

        private void AppendSymbolsWithName(ArrayBuilder<ISymbol> results, string name, Binder binder, NamespaceOrTypeSymbol container, LookupOptions options, LookupSymbolsInfo info)
        {
            LookupSymbolsInfo.IArityEnumerable arities;
            Symbol uniqueSymbol;

            if (info.TryGetAritiesAndUniqueSymbol(name, out arities, out uniqueSymbol))
            {
                if ((object)uniqueSymbol != null)
                {
                    // This name mapped to something unique.  We don't need to proceed
                    // with a costly lookup.  Just add it straight to the results.
                    results.Add(RemapSymbolIfNecessary(uniqueSymbol).GetPublicSymbol());
                }
                else
                {
                    // The name maps to multiple symbols. Actually do a real lookup so 
                    // that we will properly figure out hiding and whatnot.
                    if (arities != null)
                    {
                        foreach (var arity in arities)
                        {
                            this.AppendSymbolsWithNameAndArity(results, name, arity, binder, container, options);
                        }
                    }
                    else
                    {
                        //non-unique symbol with non-zero arity doesn't seem possible.
                        this.AppendSymbolsWithNameAndArity(results, name, 0, binder, container, options);
                    }
                }
            }
        }

        private void AppendSymbolsWithNameAndArity(
            ArrayBuilder<ISymbol> results,
            string name,
            int arity,
            Binder binder,
            NamespaceOrTypeSymbol container,
            LookupOptions options)
        {
            Debug.Assert(results != null);

            // Don't need to de-dup since AllMethodsOnArityZero can't be set at this point (not exposed in CommonLookupOptions).
            Debug.Assert((options & LookupOptions.AllMethodsOnArityZero) == 0);

            var lookupResult = LookupResult.GetInstance();

            var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
            binder.LookupSymbolsSimpleName(
                lookupResult,
                container,
                name,
                arity,
                basesBeingResolved: null,
                options: options & ~LookupOptions.IncludeExtensionMembers,
                diagnose: false,
                useSiteInfo: ref discardedUseSiteInfo);

            if (lookupResult.IsMultiViable)
            {
                if (lookupResult.Symbols.Any(t => t.Kind == SymbolKind.NamedType || t.Kind == SymbolKind.Namespace || t.Kind == SymbolKind.ErrorType))
                {
                    // binder.ResultSymbol is defined only for type/namespace lookups
                    bool wasError;
                    Symbol singleSymbol = binder.ResultSymbol(lookupResult, name, arity, this.Root, BindingDiagnosticBag.Discarded, true, out wasError, container, options);

                    if (!wasError)
                    {
                        results.Add(RemapSymbolIfNecessary(singleSymbol).GetPublicSymbol());
                    }
                    else
                    {
                        foreach (var symbol in lookupResult.Symbols)
                        {
                            results.Add(RemapSymbolIfNecessary(symbol).GetPublicSymbol());
                        }
                    }
                }
                else
                {
                    foreach (var symbol in lookupResult.Symbols)
                    {
                        results.Add(RemapSymbolIfNecessary(symbol).GetPublicSymbol());
                    }
                }
            }

            lookupResult.Free();
        }

        private Symbol RemapSymbolIfNecessary(Symbol symbol)
        {
            switch (symbol)
            {
                case LocalSymbol _:
                case ParameterSymbol _:
                case MethodSymbol { MethodKind: MethodKind.LambdaMethod }:
                    return RemapSymbolIfNecessaryCore(symbol);

                default:
                    return symbol;
            }
        }

        /// <summary>
        /// Remaps a local, parameter, localfunction, or lambda symbol, if that symbol or its containing
        /// symbols were reinferred. This should only be called when nullable semantic analysis is enabled.
        /// </summary>
        internal abstract Symbol RemapSymbolIfNecessaryCore(Symbol symbol);

        /// <summary>
        /// Determines if the symbol is accessible from the specified location. 
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and
        /// accessibility. This character position must be within the FullSpan of the Root syntax
        /// node in this SemanticModel.
        /// </param>
        /// <param name="symbol">The symbol that we are checking to see if it accessible.</param>
        /// <returns>
        /// True if "symbol is accessible, false otherwise.</returns>
        /// <remarks>
        /// This method only checks accessibility from the point of view of the accessibility
        /// modifiers on symbol and its containing types. Even if true is returned, the given symbol
        /// may not be able to be referenced for other reasons, such as name hiding.
        /// </remarks>
        public bool IsAccessible(int position, Symbol symbol)
        {
            position = CheckAndAdjustPosition(position);

            if ((object)symbol == null)
            {
                throw new ArgumentNullException(nameof(symbol));
            }

            var binder = this.GetEnclosingBinder(position);
            if (binder != null)
            {
                var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                return binder.IsAccessible(symbol, ref discardedUseSiteInfo, null);
            }

            return false;
        }

        /// <summary>
        /// Field-like events can be used as fields in types that can access private
        /// members of the declaring type of the event.
        /// </summary>
        public bool IsEventUsableAsField(int position, EventSymbol symbol)
        {
            return symbol is object && symbol.HasAssociatedField && this.IsAccessible(position, symbol.AssociatedField); //calls CheckAndAdjustPosition
        }

        private bool IsInTypeofExpression(int position)
        {
            var token = this.Root.FindToken(position);
            var curr = token.Parent;
            while (curr != this.Root)
            {
                if (curr.IsKind(SyntaxKind.TypeOfExpression))
                {
                    return true;
                }

                curr = curr.ParentOrStructuredTriviaParent;
            }

            return false;
        }

        // Gets the semantic info from a specific bound node and a set of diagnostics
        // lowestBoundNode: The lowest node in the bound tree associated with node
        // highestBoundNode: The highest node in the bound tree associated with node
        // boundNodeForSyntacticParent: The lowest node in the bound tree associated with node.Parent.
        // binderOpt: If this is null, then the one enclosing the bound node's syntax will be used (unsafe during speculative binding).
        [PerformanceSensitive(
            "https://github.com/dotnet/roslyn/issues/23582",
            Constraint = "Provide " + nameof(ArrayBuilder<Symbol>) + " capacity to reduce number of allocations.")]
        internal SymbolInfo GetSymbolInfoForNode(
            SymbolInfoOptions options,
            BoundNode lowestBoundNode,
            BoundNode highestBoundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt)
        {
            BoundExpression boundExpr;

            switch (highestBoundNode)
            {
                case BoundRecursivePattern pat:
                    return GetSymbolInfoForDeconstruction(pat);
            }
            switch (lowestBoundNode)
            {
                case BoundPositionalSubpattern subpattern:
                    return GetSymbolInfoForSubpattern(subpattern.Symbol);
                case BoundPropertySubpattern subpattern:
                    return GetSymbolInfoForSubpattern(subpattern.Member?.Symbol);
                case BoundPropertySubpatternMember subpatternMember:
                    return GetSymbolInfoForSubpattern(subpatternMember.Symbol);
                case BoundExpression boundExpr2:
                    boundExpr = boundExpr2;
                    break;
                default:
                    return SymbolInfo.None;
            }

            // TODO: Should parenthesized expression really not have symbols? At least for C#, I'm not sure that
            // is right. For example, C# allows the assignment statement:
            //    (i) = 9;
            // So we don't think this code should special case parenthesized expressions.

            // Get symbols and result kind from the lowest and highest nodes associated with the
            // syntax node.
            OneOrMany<Symbol> symbols = GetSemanticSymbols(
                boundExpr, boundNodeForSyntacticParent, binderOpt, options, out bool isDynamic, out LookupResultKind resultKind, out ImmutableArray<Symbol> unusedMemberGroup);

            if (highestBoundNode is BoundExpression highestBoundExpr)
            {
                LookupResultKind highestResultKind;
                bool highestIsDynamic;
                ImmutableArray<Symbol> unusedHighestMemberGroup;
                OneOrMany<Symbol> highestSymbols = GetSemanticSymbols(
                    highestBoundExpr, boundNodeForSyntacticParent, binderOpt, options, out highestIsDynamic, out highestResultKind, out unusedHighestMemberGroup);

                if ((symbols.Count != 1 || resultKind == LookupResultKind.OverloadResolutionFailure) && highestSymbols.Count > 0)
                {
                    symbols = highestSymbols;
                    resultKind = highestResultKind;
                    isDynamic = highestIsDynamic;
                }
                else if (highestResultKind != LookupResultKind.Empty && highestResultKind < resultKind)
                {
                    resultKind = highestResultKind;
                    isDynamic = highestIsDynamic;
                }
                else if (highestBoundExpr.Kind == BoundKind.TypeOrValueExpression)
                {
                    symbols = highestSymbols;
                    resultKind = highestResultKind;
                    isDynamic = highestIsDynamic;
                }
                else if (highestBoundExpr.Kind == BoundKind.UnaryOperator)
                {
                    if (IsUserDefinedTrueOrFalse((BoundUnaryOperator)highestBoundExpr))
                    {
                        symbols = highestSymbols;
                        resultKind = highestResultKind;
                        isDynamic = highestIsDynamic;
                    }
                    else
                    {
                        Debug.Assert(ReferenceEquals(lowestBoundNode, highestBoundNode), "How is it that this operator has the same syntax node as its operand?");
                    }
                }
            }

            if (resultKind == LookupResultKind.Empty)
            {
                // Empty typically indicates an error symbol that was created because no real
                // symbol actually existed.
                return SymbolInfoFactory.Create(ImmutableArray<Symbol>.Empty, LookupResultKind.Empty, isDynamic);
            }
            else
            {
                // Caas clients don't want ErrorTypeSymbol in the symbols, but the best guess
                // instead. If no best guess, then nothing is returned.
                var builder = ArrayBuilder<Symbol>.GetInstance(symbols.Count);
                foreach (Symbol symbol in symbols)
                {
                    AddUnwrappingErrorTypes(builder, symbol);
                }

                symbols = builder.ToOneOrManyAndFree();
            }

            if ((options & SymbolInfoOptions.ResolveAliases) != 0)
            {
                symbols = UnwrapAliases(symbols);
            }

            if (resultKind == LookupResultKind.Viable && symbols.Count > 1)
            {
                resultKind = LookupResultKind.OverloadResolutionFailure;
            }

            return SymbolInfoFactory.Create(symbols, resultKind, isDynamic);
        }

        private static SymbolInfo GetSymbolInfoForSubpattern(Symbol subpatternSymbol)
        {
            if (subpatternSymbol?.OriginalDefinition is ErrorTypeSymbol originalErrorType)
            {
                return new SymbolInfo(originalErrorType.CandidateSymbols.GetPublicSymbols(), originalErrorType.ResultKind.ToCandidateReason());
            }

            return new SymbolInfo(subpatternSymbol.GetPublicSymbol());
        }

        private SymbolInfo GetSymbolInfoForDeconstruction(BoundRecursivePattern pat)
        {
            return new SymbolInfo(pat.DeconstructMethod.GetPublicSymbol());
        }

        private static void AddUnwrappingErrorTypes(ArrayBuilder<Symbol> builder, Symbol s)
        {
            var originalErrorSymbol = s.OriginalDefinition as ErrorTypeSymbol;
            if ((object)originalErrorSymbol != null)
            {
                builder.AddRange(originalErrorSymbol.CandidateSymbols);
            }
            else
            {
                builder.Add(s);
            }
        }

        private static bool IsUserDefinedTrueOrFalse(BoundUnaryOperator @operator)
        {
            UnaryOperatorKind operatorKind = @operator.OperatorKind;
            return operatorKind == UnaryOperatorKind.UserDefinedTrue || operatorKind == UnaryOperatorKind.UserDefinedFalse;
        }

        // Gets the semantic info from a specific bound node and a set of diagnostics
        // lowestBoundNode: The lowest node in the bound tree associated with node
        // highestBoundNode: The highest node in the bound tree associated with node
        // boundNodeForSyntacticParent: The lowest node in the bound tree associated with node.Parent.
        internal CSharpTypeInfo GetTypeInfoForNode(
            BoundNode lowestBoundNode,
            BoundNode highestBoundNode,
            BoundNode boundNodeForSyntacticParent)
        {
            BoundPattern pattern = lowestBoundNode as BoundPattern ?? highestBoundNode as BoundPattern ?? (highestBoundNode is BoundSubpattern sp ? sp.Pattern : null);
            if (pattern != null)
            {
                var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                // https://github.com/dotnet/roslyn/issues/35032: support patterns
                return new CSharpTypeInfo(
                    pattern.InputType, pattern.NarrowedType, nullability: default, convertedNullability: default,
                    Compilation.Conversions.ClassifyBuiltInConversion(pattern.InputType, pattern.NarrowedType, isChecked: false, ref discardedUseSiteInfo));
            }
            if (lowestBoundNode is BoundPropertySubpatternMember member)
            {
                return new CSharpTypeInfo(member.Type, member.Type, nullability: default, convertedNullability: default, Conversion.Identity);
            }

            var boundExpr = lowestBoundNode as BoundExpression;
            var highestBoundExpr = highestBoundNode as BoundExpression;

            if (boundExpr != null &&
                !(boundNodeForSyntacticParent != null &&
                  boundNodeForSyntacticParent.Syntax.Kind() == SyntaxKind.ObjectCreationExpression &&
                  ((ObjectCreationExpressionSyntax)boundNodeForSyntacticParent.Syntax).Type == boundExpr.Syntax)) // Do not return any type information for a ObjectCreationExpressionSyntax.Type node.
            {
                // TODO: Should parenthesized expression really not have symbols? At least for C#, I'm not sure that 
                // is right. For example, C# allows the assignment statement:
                //    (i) = 9;  
                // So I don't assume this code should special case parenthesized expressions.
                TypeSymbol type = null;
                NullabilityInfo nullability = boundExpr.TopLevelNullability;

                if (boundExpr.HasExpressionType())
                {
                    type = boundExpr.Type;

                    switch (boundExpr)
                    {
                        case BoundLocal local:
                            {
                                // Use of local before declaration requires some additional fixup.
                                // Due to complications around implicit locals and type inference, we do not
                                // try to obtain a type of a local when it is used before declaration, we use
                                // a special error type symbol. However, semantic model should return the same
                                // type information for usage of a local before and after its declaration.
                                // We will detect the use before declaration cases and replace the error type
                                // symbol with the one obtained from the local. It should be safe to get the type
                                // from the local at this point.
                                if (type is ExtendedErrorTypeSymbol extended && extended.VariableUsedBeforeDeclaration)
                                {
                                    type = local.LocalSymbol.Type;
                                    nullability = local.LocalSymbol.TypeWithAnnotations.NullableAnnotation.ToNullabilityInfo(type);
                                }
                                break;
                            }
                        case BoundConvertedTupleLiteral { SourceTuple: BoundTupleLiteral original }:
                            {
                                // The bound tree fully binds tuple literals. From the language point of
                                // view, however, converted tuple literals represent tuple conversions
                                // from tuple literal expressions which may or may not have types
                                type = original.Type;
                                break;
                            }
                    }
                }

                // we match highestBoundExpr.Kind to various kind frequently, so cache it here.
                // use NoOp kind for the case when highestBoundExpr == null - NoOp will not match anything below.
                var highestBoundExprKind = highestBoundExpr?.Kind ?? BoundKind.NoOpStatement;
                TypeSymbol convertedType;
                NullabilityInfo convertedNullability;
                Conversion conversion;

                if (highestBoundExprKind == BoundKind.Lambda) // the enclosing conversion is explicit
                {
                    var lambda = (BoundLambda)highestBoundExpr;
                    convertedType = lambda.Type;
                    // The bound tree always fully binds lambda and anonymous functions. From the language point of
                    // view, however, anonymous functions converted to a real delegate type should only have a 
                    // ConvertedType, not a Type. So set Type to null here. Otherwise you get the edge case where both
                    // Type and ConvertedType are the same, but the conversion isn't Identity.
                    type = null;
                    nullability = default;
                    convertedNullability = new NullabilityInfo(CodeAnalysis.NullableAnnotation.NotAnnotated, CodeAnalysis.NullableFlowState.NotNull);
                    conversion = new Conversion(ConversionKind.AnonymousFunction, lambda.Symbol, false);
                }
                else if ((highestBoundExpr as BoundConversion)?.Conversion.IsTupleLiteralConversion == true)
                {
                    var tupleLiteralConversion = (BoundConversion)highestBoundExpr;
                    if (tupleLiteralConversion.Operand.Kind == BoundKind.ConvertedTupleLiteral)
                    {
                        var convertedTuple = (BoundConvertedTupleLiteral)tupleLiteralConversion.Operand;
                        type = convertedTuple.SourceTuple.Type;
                        nullability = convertedTuple.TopLevelNullability;
                    }
                    else
                    {
                        (type, nullability) = getTypeAndNullability(tupleLiteralConversion.Operand);
                    }

                    (convertedType, convertedNullability) = getTypeAndNullability(tupleLiteralConversion);
                    conversion = tupleLiteralConversion.Conversion;
                }
                else if (highestBoundExprKind == BoundKind.FixedLocalCollectionInitializer)
                {
                    var initializer = (BoundFixedLocalCollectionInitializer)highestBoundExpr;
                    (convertedType, convertedNullability) = getTypeAndNullability(initializer);
                    (type, nullability) = getTypeAndNullability(initializer.Expression);

                    // the most pertinent conversion is the pointer conversion 
                    conversion = BoundNode.GetConversion(initializer.ElementPointerConversion, initializer.ElementPointerPlaceholder);
                }
                else if (boundExpr is BoundConvertedSwitchExpression { WasTargetTyped: true } convertedSwitch)
                {
                    if (highestBoundExpr is BoundConversion { ConversionKind: ConversionKind.SwitchExpression, Conversion: var convertedSwitchConversion })
                    {
                        // There was an implicit cast.
                        type = convertedSwitch.NaturalTypeOpt;
                        convertedType = convertedSwitch.Type;
                        convertedNullability = convertedSwitch.TopLevelNullability;
                        conversion = convertedSwitchConversion.IsValid ? convertedSwitchConversion : Conversion.NoConversion;
                    }
                    else
                    {
                        // There was an explicit cast on top of this
                        type = convertedSwitch.NaturalTypeOpt;
                        (convertedType, convertedNullability) = (type, nullability);
                        conversion = Conversion.Identity;
                    }
                }
                else if (boundExpr is BoundConditionalOperator { WasTargetTyped: true } cond)
                {
                    if (highestBoundExpr is BoundConversion { ConversionKind: ConversionKind.ConditionalExpression })
                    {
                        // There was an implicit cast.
                        type = cond.NaturalTypeOpt;
                        convertedType = cond.Type;
                        convertedNullability = nullability;
                        conversion = Conversion.MakeConditionalExpression(ImmutableArray<Conversion>.Empty);
                    }
                    else
                    {
                        // There was an explicit cast on top of this.
                        type = cond.NaturalTypeOpt;
                        (convertedType, convertedNullability) = (type, nullability);
                        conversion = Conversion.Identity;
                    }
                }
                else if (boundExpr is BoundCollectionExpression convertedCollection)
                {
                    type = null;
                    if (highestBoundExpr is BoundConversion { ConversionKind: ConversionKind.CollectionExpression or ConversionKind.NoConversion, Conversion: var convertedCollectionConversion })
                    {
                        convertedType = highestBoundExpr.Type;
                        convertedNullability = convertedCollection.TopLevelNullability;
                        conversion = convertedCollectionConversion;
                    }
                    else if (highestBoundExpr is BoundConversion { ConversionKind: ConversionKind.ImplicitNullable, Conversion.UnderlyingConversions: [{ Kind: ConversionKind.CollectionExpression }] } boundConversion)
                    {
                        convertedType = highestBoundExpr.Type;
                        convertedNullability = convertedCollection.TopLevelNullability;
                        conversion = boundConversion.Conversion;
                    }
                    else
                    {
                        // Explicit cast or error scenario like `object x = [];`
                        convertedNullability = nullability;
                        convertedType = null;
                        conversion = Conversion.Identity;
                    }
                }
                else if (highestBoundExpr != null && highestBoundExpr != boundExpr && highestBoundExpr.HasExpressionType())
                {
                    (convertedType, convertedNullability) = getTypeAndNullability(highestBoundExpr);
                    if (highestBoundExprKind != BoundKind.Conversion)
                    {
                        conversion = Conversion.Identity;
                    }
                    else if (((BoundConversion)highestBoundExpr).Operand.Kind != BoundKind.Conversion)
                    {
                        conversion = highestBoundExpr.GetConversion();
                        if (conversion.Kind == ConversionKind.AnonymousFunction)
                        {
                            // See comment above: anonymous functions do not have a type
                            type = null;
                            nullability = default;
                        }
                    }
                    else
                    {
                        // There is a sequence of conversions; we use ClassifyConversionFromExpression to report the most pertinent.
                        var binder = this.GetEnclosingBinder(boundExpr.Syntax.Span.Start);
                        var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                        conversion = binder.Conversions.ClassifyConversionFromExpression(boundExpr, convertedType, isChecked: ((BoundConversion)highestBoundExpr).Checked, ref discardedUseSiteInfo);
                    }
                }
                else if (boundNodeForSyntacticParent?.Kind == BoundKind.DelegateCreationExpression)
                {
                    // A delegate creation expression takes the place of a method group or anonymous function conversion.
                    var delegateCreation = (BoundDelegateCreationExpression)boundNodeForSyntacticParent;
                    (convertedType, convertedNullability) = getTypeAndNullability(delegateCreation);
                    switch (boundExpr.Kind)
                    {
                        case BoundKind.MethodGroup:
                            {
                                conversion = new Conversion(ConversionKind.MethodGroup, delegateCreation.MethodOpt, delegateCreation.IsExtensionMethod);
                                break;
                            }
                        case BoundKind.Lambda:
                            {
                                var lambda = (BoundLambda)boundExpr;
                                conversion = new Conversion(ConversionKind.AnonymousFunction, lambda.Symbol, delegateCreation.IsExtensionMethod);
                                break;
                            }
                        case BoundKind.UnboundLambda:
                            {
                                var lambda = ((UnboundLambda)boundExpr).BindForErrorRecovery();
                                conversion = new Conversion(ConversionKind.AnonymousFunction, lambda.Symbol, delegateCreation.IsExtensionMethod);
                                break;
                            }
                        default:
                            conversion = Conversion.Identity;
                            break;
                    }
                }
                else if (boundExpr is BoundConversion { ConversionKind: ConversionKind.MethodGroup, Conversion: var exprConversion, Type: { TypeKind: TypeKind.FunctionPointer }, SymbolOpt: var symbol })
                {
                    // Because the method group is a separate syntax node from the &, the lowest bound node here is the BoundConversion. However,
                    // the conversion represents an implicit method group conversion from a typeless method group to a function pointer type, so
                    // we should reflect that in the types and conversion we return.
                    convertedType = type;
                    convertedNullability = nullability;
                    conversion = exprConversion;
                    type = null;
                    nullability = new NullabilityInfo(CodeAnalysis.NullableAnnotation.NotAnnotated, CodeAnalysis.NullableFlowState.NotNull);
                }
                else
                {
                    convertedType = type;
                    convertedNullability = nullability;
                    conversion = Conversion.Identity;
                }

                return new CSharpTypeInfo(type, convertedType, nullability, convertedNullability, conversion);
            }

            return CSharpTypeInfo.None;

            static (TypeSymbol, NullabilityInfo) getTypeAndNullability(BoundExpression expr) => (expr.Type, expr.TopLevelNullability);
        }

        // Gets the method or property group from a specific bound node.
        // lowestBoundNode: The lowest node in the bound tree associated with node
        // highestBoundNode: The highest node in the bound tree associated with node
        // boundNodeForSyntacticParent: The lowest node in the bound tree associated with node.Parent.
        internal ImmutableArray<Symbol> GetMemberGroupForNode(
            SymbolInfoOptions options,
            BoundNode lowestBoundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt)
        {
            if (lowestBoundNode is BoundExpression boundExpr)
            {
                LookupResultKind resultKind;
                ImmutableArray<Symbol> memberGroup;
                bool isDynamic;
                GetSemanticSymbols(boundExpr, boundNodeForSyntacticParent, binderOpt, options, out isDynamic, out resultKind, out memberGroup);

                return memberGroup;
            }

            return ImmutableArray<Symbol>.Empty;
        }

        // Gets the indexer group from a specific bound node.
        // lowestBoundNode: The lowest node in the bound tree associated with node
        // highestBoundNode: The highest node in the bound tree associated with node
        // boundNodeForSyntacticParent: The lowest node in the bound tree associated with node.Parent.
        internal ImmutableArray<IPropertySymbol> GetIndexerGroupForNode(
            BoundNode lowestBoundNode,
            Binder binderOpt)
        {
            var boundExpr = lowestBoundNode as BoundExpression;
            if (boundExpr != null && boundExpr.Kind != BoundKind.TypeExpression)
            {
                return GetIndexerGroupSemanticSymbols(boundExpr, binderOpt);
            }

            return ImmutableArray<IPropertySymbol>.Empty;
        }

        // Gets symbol info for a type or namespace or alias reference. It is assumed that any error cases will come in
        // as a type whose OriginalDefinition is an error symbol from which the ResultKind can be retrieved.
        internal static SymbolInfo GetSymbolInfoForSymbol(Symbol symbol, SymbolInfoOptions options)
        {
            Debug.Assert((object)symbol != null);

            // Determine type. Dig through aliases if necessary.
            Symbol unwrapped = UnwrapAlias(symbol);
            TypeSymbol type = unwrapped as TypeSymbol;

            // Determine symbols and resultKind.
            var originalErrorSymbol = (object)type != null ? type.OriginalDefinition as ErrorTypeSymbol : null;

            if ((object)originalErrorSymbol != null)
            {
                // Error case.
                var symbols = OneOrMany<Symbol>.Empty;

                LookupResultKind resultKind = originalErrorSymbol.ResultKind;
                if (resultKind != LookupResultKind.Empty)
                {
                    symbols = OneOrMany.Create(originalErrorSymbol.CandidateSymbols);
                }

                if ((options & SymbolInfoOptions.ResolveAliases) != 0)
                {
                    symbols = UnwrapAliases(symbols);
                }

                return SymbolInfoFactory.Create(symbols, resultKind, isDynamic: false);
            }
            else
            {
                // Non-error case. Use constructor that doesn't require creation of a Symbol array.
                var symbolToReturn = ((options & SymbolInfoOptions.ResolveAliases) != 0) ? unwrapped : symbol;
                return new SymbolInfo(symbolToReturn.GetPublicSymbol());
            }
        }

        // Gets TypeInfo for a type or namespace or alias reference.
        internal static CSharpTypeInfo GetTypeInfoForSymbol(Symbol symbol)
        {
            Debug.Assert((object)symbol != null);

            // Determine type. Dig through aliases if necessary.
            TypeSymbol type = UnwrapAlias(symbol) as TypeSymbol;
            // https://github.com/dotnet/roslyn/issues/35033: Examine this and make sure that we're using the correct nullabilities
            return new CSharpTypeInfo(type, type, default, default, Conversion.Identity);
        }

        protected static Symbol UnwrapAlias(Symbol symbol)
        {
            return symbol is AliasSymbol aliasSym ? aliasSym.Target : symbol;
        }

        protected static OneOrMany<Symbol> UnwrapAliases(OneOrMany<Symbol> symbols)
        {
            bool anyAliases = false;

            foreach (Symbol symbol in symbols)
            {
                if (symbol.Kind == SymbolKind.Alias)
                    anyAliases = true;
            }

            if (!anyAliases)
                return symbols;

            ArrayBuilder<Symbol> builder = ArrayBuilder<Symbol>.GetInstance();
            foreach (Symbol symbol in symbols)
            {
                // Caas clients don't want ErrorTypeSymbol in the symbols, but the best guess
                // instead. If no best guess, then nothing is returned.
                AddUnwrappingErrorTypes(builder, UnwrapAlias(symbol));
            }

            return builder.ToOneOrManyAndFree();
        }

        // This is used by other binding APIs to invoke the right binder API
        internal virtual BoundNode Bind(Binder binder, CSharpSyntaxNode node, BindingDiagnosticBag diagnostics)
        {
            if (Compilation.TestOnlyCompilationData is MemberSemanticModel.MemberSemanticBindingCounter counter)
            {
                counter.BindCount++;
            }

            switch (node)
            {
                case ExpressionSyntax expression:
                    var parent = expression.Parent;
                    return parent.IsKind(SyntaxKind.GotoStatement)
                        ? binder.BindLabel(expression, diagnostics)
                        : binder.BindNamespaceOrTypeOrExpression(expression, diagnostics);
                case StatementSyntax statement:
                    return binder.BindStatement(statement, diagnostics);
                case GlobalStatementSyntax globalStatement:
                    BoundStatement bound = binder.BindStatement(globalStatement.Statement, diagnostics);
                    return new BoundGlobalStatementInitializer(node, bound);
            }

            return null;
        }

        /// <summary>
        /// Analyze control-flow within a part of a method body. 
        /// </summary>
        /// <param name="firstStatement">The first statement to be included in the analysis.</param>
        /// <param name="lastStatement">The last statement to be included in the analysis.</param>
        /// <returns>An object that can be used to obtain the result of the control flow analysis.</returns>
        /// <exception cref="ArgumentException">The two statements are not contained within the same statement list.</exception>
        public virtual ControlFlowAnalysis AnalyzeControlFlow(StatementSyntax firstStatement, StatementSyntax lastStatement)
        {
            // Only supported on a SyntaxTreeSemanticModel.
            throw new NotSupportedException();
        }

        /// <summary>
        /// Analyze control-flow within a part of a method body. 
        /// </summary>
        /// <param name="statement">The statement to be included in the analysis.</param>
        /// <returns>An object that can be used to obtain the result of the control flow analysis.</returns>
        public virtual ControlFlowAnalysis AnalyzeControlFlow(StatementSyntax statement)
        {
            return AnalyzeControlFlow(statement, statement);
        }

        /// <summary>
        /// Analyze data-flow within an <see cref="ConstructorInitializerSyntax"/>. 
        /// </summary>
        /// <param name="constructorInitializer">The ctor-init within the associated SyntaxTree to analyze.</param>
        /// <returns>An object that can be used to obtain the result of the data flow analysis.</returns>
        public virtual DataFlowAnalysis AnalyzeDataFlow(ConstructorInitializerSyntax constructorInitializer)
        {
            // Only supported on a SyntaxTreeSemanticModel.
            throw new NotSupportedException();
        }

        /// <summary>
        /// Analyze data-flow within an <see cref="PrimaryConstructorBaseTypeSyntax.ArgumentList"/>. 
        /// </summary>
        /// <param name="primaryConstructorBaseType">The node within the associated SyntaxTree to analyze.</param>
        /// <returns>An object that can be used to obtain the result of the data flow analysis.</returns>
        public virtual DataFlowAnalysis AnalyzeDataFlow(PrimaryConstructorBaseTypeSyntax primaryConstructorBaseType)
        {
            // Only supported on a SyntaxTreeSemanticModel.
            throw new NotSupportedException();
        }

        /// <summary>
        /// Analyze data-flow within an <see cref="ExpressionSyntax"/>. 
        /// </summary>
        /// <param name="expression">The expression within the associated SyntaxTree to analyze.</param>
        /// <returns>An object that can be used to obtain the result of the data flow analysis.</returns>
        public virtual DataFlowAnalysis AnalyzeDataFlow(ExpressionSyntax expression)
        {
            // Only supported on a SyntaxTreeSemanticModel.
            throw new NotSupportedException();
        }

        /// <summary>
        /// Analyze data-flow within a part of a method body. 
        /// </summary>
        /// <param name="firstStatement">The first statement to be included in the analysis.</param>
        /// <param name="lastStatement">The last statement to be included in the analysis.</param>
        /// <returns>An object that can be used to obtain the result of the data flow analysis.</returns>
        /// <exception cref="ArgumentException">The two statements are not contained within the same statement list.</exception>
        public virtual DataFlowAnalysis AnalyzeDataFlow(StatementSyntax firstStatement, StatementSyntax lastStatement)
        {
            // Only supported on a SyntaxTreeSemanticModel.
            throw new NotSupportedException();
        }

        /// <summary>
        /// Analyze data-flow within a part of a method body. 
        /// </summary>
        /// <param name="statement">The statement to be included in the analysis.</param>
        /// <returns>An object that can be used to obtain the result of the data flow analysis.</returns>
        public virtual DataFlowAnalysis AnalyzeDataFlow(StatementSyntax statement)
        {
            return AnalyzeDataFlow(statement, statement);
        }

        /// <summary>
        /// Get a SemanticModel object that is associated with a method body that did not appear in this source code.
        /// Given <paramref name="position"/> must lie within an existing method body of the Root syntax node for this SemanticModel.
        /// Locals and labels declared within this existing method body are not considered to be in scope of the speculated method body.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel and must be
        /// within the FullSpan of a Method body within the Root syntax node.</param>
        /// <param name="method">A syntax node that represents a parsed method declaration. This method should not be
        /// present in the syntax tree associated with this object, but must have identical signature to the method containing
        /// the given <paramref name="position"/> in this SemanticModel.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="method"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="method"/> node is contained any SyntaxTree in the current Compilation</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="method"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModelForMethodBody(int position, BaseMethodDeclarationSyntax method, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(method);
            var result = TryGetSpeculativeSemanticModelForMethodBodyCore((SyntaxTreeSemanticModel)this, position, method, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelForMethodBodyCore(SyntaxTreeSemanticModel parentModel, int position, BaseMethodDeclarationSyntax method, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a method body that did not appear in this source code.
        /// Given <paramref name="position"/> must lie within an existing method body of the Root syntax node for this SemanticModel.
        /// Locals and labels declared within this existing method body are not considered to be in scope of the speculated method body.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel and must be
        /// within the FullSpan of a Method body within the Root syntax node.</param>
        /// <param name="accessor">A syntax node that represents a parsed accessor declaration. This accessor should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="accessor"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="accessor"/> node is contained any SyntaxTree in the current Compilation</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="accessor"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModelForMethodBody(int position, AccessorDeclarationSyntax accessor, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(accessor);
            var result = TryGetSpeculativeSemanticModelForMethodBodyCore((SyntaxTreeSemanticModel)this, position, accessor, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelForMethodBodyCore(SyntaxTreeSemanticModel parentModel, int position, AccessorDeclarationSyntax accessor, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a type syntax node that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a type syntax that did not appear in source code. 
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// </param>
        /// <param name="type">A syntax node that represents a parsed expression. This expression should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="bindingOption">Indicates whether to bind the expression as a full expression,
        /// or as a type or namespace.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="type"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="type"/> node is contained any SyntaxTree in the current Compilation</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="type"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, TypeSyntax type, out SemanticModel speculativeModel, SpeculativeBindingOption bindingOption = SpeculativeBindingOption.BindAsExpression)
        {
            CheckModelAndSyntaxNodeToSpeculate(type);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, type, bindingOption, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, TypeSyntax type, SpeculativeBindingOption bindingOption, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a statement that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a statement that did not appear in source code. 
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.</param>
        /// <param name="statement">A syntax node that represents a parsed statement. This statement should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="statement"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="statement"/> node is contained any SyntaxTree in the current Compilation</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="statement"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, StatementSyntax statement, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(statement);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, statement, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, StatementSyntax statement, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with an initializer that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a field initializer or default parameter value that did not appear in source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// </param>
        /// <param name="initializer">A syntax node that represents a parsed initializer. This initializer should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="initializer"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="initializer"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="initializer"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, EqualsValueClauseSyntax initializer, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(initializer);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, initializer, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, EqualsValueClauseSyntax initializer, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with an expression body that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of an expression body that did not appear in source code.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// </param>
        /// <param name="expressionBody">A syntax node that represents a parsed expression body. This node should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="expressionBody"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="expressionBody"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="expressionBody"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, ArrowExpressionClauseSyntax expressionBody, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(expressionBody);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, expressionBody, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, ArrowExpressionClauseSyntax expressionBody, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a constructor initializer that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a constructor initializer that did not appear in source code. 
        /// 
        /// NOTE: This will only work in locations where there is already a constructor initializer.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// Furthermore, it must be within the span of an existing constructor initializer.
        /// </param>
        /// <param name="constructorInitializer">A syntax node that represents a parsed constructor initializer.
        /// This node should not be present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="constructorInitializer"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="constructorInitializer"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="constructorInitializer"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, ConstructorInitializerSyntax constructorInitializer, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(constructorInitializer);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, constructorInitializer, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, ConstructorInitializerSyntax constructorInitializer, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a constructor initializer that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a constructor initializer that did not appear in source code. 
        /// 
        /// NOTE: This will only work in locations where there is already a constructor initializer.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the span of an existing constructor initializer.
        /// </param>
        /// <param name="constructorInitializer">A syntax node that represents a parsed constructor initializer.
        /// This node should not be present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="constructorInitializer"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="constructorInitializer"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="constructorInitializer"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, PrimaryConstructorBaseTypeSyntax constructorInitializer, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(constructorInitializer);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, constructorInitializer, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, PrimaryConstructorBaseTypeSyntax constructorInitializer, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with a cref that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of a cref that did not appear in source code. 
        /// 
        /// NOTE: This will only work in locations where there is already a cref.
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.
        /// Furthermore, it must be within the span of an existing cref.
        /// </param>
        /// <param name="crefSyntax">A syntax node that represents a parsed cref syntax.
        /// This node should not be present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="crefSyntax"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="crefSyntax"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="crefSyntax"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, CrefSyntax crefSyntax, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(crefSyntax);
            var result = TryGetSpeculativeSemanticModelCore((SyntaxTreeSemanticModel)this, position, crefSyntax, out PublicSemanticModel speculativeSyntaxTreeModel);
            speculativeModel = speculativeSyntaxTreeModel;
            return result;
        }

        internal abstract bool TryGetSpeculativeSemanticModelCore(SyntaxTreeSemanticModel parentModel, int position, CrefSyntax crefSyntax, out PublicSemanticModel speculativeModel);

        /// <summary>
        /// Get a SemanticModel object that is associated with an attribute that did not appear in
        /// this source code. This can be used to get detailed semantic information about sub-parts
        /// of an attribute that did not appear in source code. 
        /// </summary>
        /// <param name="position">A character position used to identify a declaration scope and accessibility. This
        /// character position must be within the FullSpan of the Root syntax node in this SemanticModel.</param>
        /// <param name="attribute">A syntax node that represents a parsed attribute. This attribute should not be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="speculativeModel">A SemanticModel object that can be used to inquire about the semantic
        /// information associated with syntax nodes within <paramref name="attribute"/>.</param>
        /// <returns>Flag indicating whether a speculative semantic model was created.</returns>
        /// <exception cref="ArgumentException">Throws this exception if the <paramref name="attribute"/> node is contained any SyntaxTree in the current Compilation.</exception>
        /// <exception cref="ArgumentNullException">Throws this exception if <paramref name="attribute"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Throws this exception if this model is a speculative semantic model, i.e. <see cref="SemanticModel.IsSpeculativeSemanticModel"/> is true.
        /// Chaining of speculative semantic model is not supported.</exception>
        public bool TryGetSpeculativeSemanticModel(int position, AttributeSyntax attribute, out SemanticModel speculativeModel)
        {
            CheckModelAndSyntaxNodeToSpeculate(attribute);

            var binder = GetSpeculativeBinderForAttribute(position, attribute);
            if (binder == null)
            {
                speculativeModel = null;
                return false;
            }

            AliasSymbol aliasOpt;
            var attributeType = (NamedTypeSymbol)binder.BindType(attribute.Name, BindingDiagnosticBag.Discarded, out aliasOpt).Type;
            speculativeModel = ((SyntaxTreeSemanticModel)this).CreateSpeculativeAttributeSemanticModel(position, attribute, binder, aliasOpt, attributeType);
            return true;
        }

        /// <summary>
        /// If this is a speculative semantic model, then returns its parent semantic model.
        /// Otherwise, returns null.
        /// </summary>
        public new abstract CSharpSemanticModel ParentModel
        {
            get;
        }

        /// <summary>
        /// The SyntaxTree that this object is associated with.
        /// </summary>
        public new abstract SyntaxTree SyntaxTree
        {
            get;
        }

        /// <summary>
        /// Determines what type of conversion, if any, would be used if a given expression was
        /// converted to a given type.  If isExplicitInSource is true, the conversion produced is
        /// that which would be used if the conversion were done for a cast expression.
        /// </summary>
        /// <param name="expression">An expression which much occur within the syntax tree
        /// associated with this object.</param>
        /// <param name="destination">The type to attempt conversion to.</param>
        /// <param name="isExplicitInSource">True if the conversion should be determined as for a cast expression.</param>
        /// <returns>Returns a Conversion object that summarizes whether the conversion was
        /// possible, and if so, what kind of conversion it was. If no conversion was possible, a
        /// Conversion object with a false "Exists" property is returned.</returns>
        /// <remarks>To determine the conversion between two types (instead of an expression and a
        /// type), use Compilation.ClassifyConversion.</remarks>
        public abstract Conversion ClassifyConversion(ExpressionSyntax expression, ITypeSymbol destination, bool isExplicitInSource = false);

        /// <summary>
        /// Determines what type of conversion, if any, would be used if a given expression was
        /// converted to a given type.  If isExplicitInSource is true, the conversion produced is
        /// that which would be used if the conversion were done for a cast expression.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration
        /// scope and accessibility.</param>
        /// <param name="expression">The expression to classify. This expression does not need to be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="destination">The type to attempt conversion to.</param>
        /// <param name="isExplicitInSource">True if the conversion should be determined as for a cast expression.</param>
        /// <returns>Returns a Conversion object that summarizes whether the conversion was
        /// possible, and if so, what kind of conversion it was. If no conversion was possible, a
        /// Conversion object with a false "Exists" property is returned.</returns>
        /// <remarks>To determine the conversion between two types (instead of an expression and a
        /// type), use Compilation.ClassifyConversion.</remarks>
        public Conversion ClassifyConversion(int position, ExpressionSyntax expression, ITypeSymbol destination, bool isExplicitInSource = false)
        {
            if ((object)destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            TypeSymbol cdestination = destination.EnsureCSharpSymbolOrNull(nameof(destination));

            if (expression.Kind() == SyntaxKind.DeclarationExpression)
            {
                // Conversion from a declaration is unspecified.
                return Conversion.NoConversion;
            }

            if (isExplicitInSource)
            {
                return ClassifyConversionForCast(position, expression, cdestination);
            }

            // Note that it is possible for an expression to be convertible to a type
            // via both an implicit user-defined conversion and an explicit built-in conversion.
            // In that case, this method chooses the implicit conversion.

            position = CheckAndAdjustPosition(position);
            var binder = this.GetEnclosingBinder(position);
            if (binder != null)
            {
                var bnode = binder.BindExpression(expression, BindingDiagnosticBag.Discarded);

                if (bnode != null && !cdestination.IsErrorType())
                {
                    var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

                    return binder.Conversions.ClassifyConversionFromExpression(bnode, cdestination, isChecked: binder.CheckOverflowAtRuntime, ref discardedUseSiteInfo);
                }
            }

            return Conversion.NoConversion;
        }

        /// <summary>
        /// Determines what type of conversion, if any, would be used if a given expression was
        /// converted to a given type using an explicit cast.
        /// </summary>
        /// <param name="expression">An expression which much occur within the syntax tree
        /// associated with this object.</param>
        /// <param name="destination">The type to attempt conversion to.</param>
        /// <returns>Returns a Conversion object that summarizes whether the conversion was
        /// possible, and if so, what kind of conversion it was. If no conversion was possible, a
        /// Conversion object with a false "Exists" property is returned.</returns>
        /// <remarks>To determine the conversion between two types (instead of an expression and a
        /// type), use Compilation.ClassifyConversion.</remarks>
        internal abstract Conversion ClassifyConversionForCast(ExpressionSyntax expression, TypeSymbol destination);

        /// <summary>
        /// Determines what type of conversion, if any, would be used if a given expression was
        /// converted to a given type using an explicit cast.
        /// </summary>
        /// <param name="position">The character position for determining the enclosing declaration
        /// scope and accessibility.</param>
        /// <param name="expression">The expression to classify. This expression does not need to be
        /// present in the syntax tree associated with this object.</param>
        /// <param name="destination">The type to attempt conversion to.</param>
        /// <returns>Returns a Conversion object that summarizes whether the conversion was
        /// possible, and if so, what kind of conversion it was. If no conversion was possible, a
        /// Conversion object with a false "Exists" property is returned.</returns>
        /// <remarks>To determine the conversion between two types (instead of an expression and a
        /// type), use Compilation.ClassifyConversion.</remarks>
        internal Conversion ClassifyConversionForCast(int position, ExpressionSyntax expression, TypeSymbol destination)
        {
            if ((object)destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            position = CheckAndAdjustPosition(position);
            var binder = this.GetEnclosingBinder(position);
            if (binder != null)
            {
                var bnode = binder.BindExpression(expression, BindingDiagnosticBag.Discarded);

                if (bnode != null && !destination.IsErrorType())
                {
                    var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

                    return binder.Conversions.ClassifyConversionFromExpression(bnode, destination, isChecked: binder.CheckOverflowAtRuntime, ref discardedUseSiteInfo, forCast: true);
                }
            }

            return Conversion.NoConversion;
        }

        #region "GetDeclaredSymbol overloads for MemberDeclarationSyntax and its subtypes"

        /// <summary>
        /// Given a member declaration syntax, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a member.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        /// <remarks>
        /// NOTE:   We have no GetDeclaredSymbol overloads for following subtypes of MemberDeclarationSyntax:
        /// NOTE:   (1) GlobalStatementSyntax as they don't declare any symbols.
        /// NOTE:   (2) IncompleteMemberSyntax as there are no symbols for incomplete members.
        /// NOTE:   (3) BaseFieldDeclarationSyntax or its subtypes as these declarations can contain multiple variable declarators.
        /// NOTE:       GetDeclaredSymbol should be called on the variable declarators directly.
        /// </remarks>
        public abstract ISymbol GetDeclaredSymbol(MemberDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a local function declaration syntax, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a member.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IMethodSymbol GetDeclaredSymbol(LocalFunctionStatementSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a compilation unit syntax, get the corresponding Simple Program entry point symbol.
        /// </summary>
        /// <param name="declarationSyntax">The compilation unit that declares the entry point member.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IMethodSymbol GetDeclaredSymbol(CompilationUnitSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a namespace declaration syntax node, get the corresponding namespace symbol for
        /// the declaration assembly.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The namespace symbol that was declared by the namespace declaration.</returns>
        public abstract INamespaceSymbol GetDeclaredSymbol(NamespaceDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a namespace declaration syntax node, get the corresponding namespace symbol for
        /// the declaration assembly.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The namespace symbol that was declared by the namespace declaration.</returns>
        public abstract INamespaceSymbol GetDeclaredSymbol(FileScopedNamespaceDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a type declaration, get the corresponding type symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a type.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The type symbol that was declared.</returns>
        /// <remarks>
        /// NOTE:   We have no GetDeclaredSymbol overloads for subtypes of BaseTypeDeclarationSyntax as all of them return a NamedTypeSymbol.
        /// </remarks>
        public abstract INamedTypeSymbol GetDeclaredSymbol(BaseTypeDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a delegate declaration, get the corresponding type symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a delegate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The type symbol that was declared.</returns>
        public abstract INamedTypeSymbol GetDeclaredSymbol(DelegateDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a enum member declaration, get the corresponding field symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares an enum member.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IFieldSymbol GetDeclaredSymbol(EnumMemberDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a base method declaration syntax, get the corresponding method symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a method.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        /// <remarks>
        /// NOTE:   We have no GetDeclaredSymbol overloads for subtypes of BaseMethodDeclarationSyntax as all of them return a MethodSymbol.
        /// </remarks>
        public abstract IMethodSymbol GetDeclaredSymbol(BaseMethodDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        #region GetDeclaredSymbol overloads for BasePropertyDeclarationSyntax and its subtypes

        /// <summary>
        /// Given a syntax node that declares a property, indexer or an event, get the corresponding declared symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a property, indexer or an event.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract ISymbol GetDeclaredSymbol(BasePropertyDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node that declares a property, get the corresponding declared symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a property.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IPropertySymbol GetDeclaredSymbol(PropertyDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node that declares an indexer, get the corresponding declared symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares an indexer.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IPropertySymbol GetDeclaredSymbol(IndexerDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node that declares a (custom) event, get the corresponding event symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a event.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IEventSymbol GetDeclaredSymbol(EventDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        #endregion

        #endregion

        // Anonymous types and Tuple expressions are an interesting case here because they declare their own types
        //
        // In both cases there is no distinct syntax that creates the type and the syntax that describes the type is the literal itself.
        // Surely - if you need to modify the anonymous type or a type of a tuple literal, you would be modifying these expressions.
        //
        // As a result we support GetDeclaredSymbol on the whole AnonymousObjectCreationExpressionSyntax/TupleExpressionSyntax.
        // The implementation returns the type of the expression.
        //
        // In addition to that GetDeclaredSymbol works on the AnonymousObjectMemberDeclaratorSyntax/ArgumentSyntax
        // The implementation returns the property/field symbol that is declared by the corresponding syntax.
        //
        // Example:
        //              GetDeclaredSymbol => Type: (int Alice, int Bob) 
        //             _____ |__________
        //            [                 ]
        // var tuple = (Alice: 1, Bob: 2);
        //                        [     ]
        //                           \GetDeclaredSymbol => Field: (int Alice, int Bob).Bob
        //
        // A special note must be made about the locations of the corresponding symbols - they refer to the actual syntax
        // of the literal or the anonymous type creation expression
        // 
        // This way IDEs can unambiguously implement such services as "Go to definition"
        //
        // I.E. GetSymbolInfo for "Bob" in "tuple.Bob" should point to the same field as returned by GetDeclaredSymbol when applied to 
        // the ArgumentSyntax "Bob: 2", since that is where the field was declared, where renames should be applied and so on.
        //                 
        //
        // In comparison to anonymous types, tuples have one special behavior. 
        // It is permitted for tuple literals to not have a natural type as long as there is a target type which determines the types of the fields.
        // As, such for the purpose of GetDeclaredSymbol, the type symbol that is returned for tuple literals has target-typed fields, 
        // but yet with the original names.
        //
        //                               GetDeclaredSymbol => Type: (string Alice, short Bob) 
        //                         ________ |__________
        //                         [                   ]
        // (string, short) tuple = (Alice: null, Bob: 2);
        //                         [           ]
        //                              \GetDeclaredSymbol => Field: (string Alice, short Bob).Alice
        //
        // In particular, the location of the field declaration is "Alice: null" and not the "string"
        //                 the location of the type is "(Alice: null, Bob: 2)" and not the "(string, short)"
        //
        // The reason for this behavior is that, even though there might not be other references to "Alice" field in the code, 
        // the name "Alice" itself evidently refers to something named "Alice" and should still work with
        // all the related APIs and services such as "Find all References", "Go to definition", "symbolic rename" etc... 
        // 
        //                         GetSymbolInfo => Field: (string Alice, short Bob).Alice 
        //                         __ |__
        //                         [     ]
        // (string, short) tuple = (Alice: null, Bob: 2);
        //

        /// <summary>
        /// Given a syntax node of anonymous object creation initializer, get the anonymous object property symbol.
        /// </summary>
        /// <param name="declaratorSyntax">The syntax node that declares a property.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IPropertySymbol GetDeclaredSymbol(AnonymousObjectMemberDeclaratorSyntax declaratorSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node of anonymous object creation expression, get the anonymous object type symbol.
        /// </summary>
        /// <param name="declaratorSyntax">The syntax node that declares an anonymous object.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract INamedTypeSymbol GetDeclaredSymbol(AnonymousObjectCreationExpressionSyntax declaratorSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node of a tuple expression, get the tuple type symbol.
        /// </summary>
        /// <param name="declaratorSyntax">The tuple expression node.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract INamedTypeSymbol GetDeclaredSymbol(TupleExpressionSyntax declaratorSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node of an argument expression, get the declared symbol.
        /// </summary>
        /// <param name="declaratorSyntax">The argument syntax node.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        /// <remarks>
        /// Generally ArgumentSyntax nodes do not declare symbols, except when used as arguments of a tuple literal.
        /// Example:  var x = (Alice: 1, Bob: 2);
        ///           ArgumentSyntax "Alice: 1" declares a tuple element field "(int Alice, int Bob).Alice"
        /// </remarks>
        public abstract ISymbol GetDeclaredSymbol(ArgumentSyntax declaratorSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node that declares a property or member accessor, get the corresponding
        /// symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares an accessor.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IMethodSymbol GetDeclaredSymbol(AccessorDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a syntax node that declares an expression body, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares an expression body.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract IMethodSymbol GetDeclaredSymbol(ArrowExpressionClauseSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a variable declarator syntax, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a variable.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract ISymbol GetDeclaredSymbol(VariableDeclaratorSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a variable designation syntax, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a variable.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public abstract ISymbol GetDeclaredSymbol(SingleVariableDesignationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a labeled statement syntax, get the corresponding label symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node of the labeled statement.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The label symbol for that label.</returns>
        public abstract ILabelSymbol GetDeclaredSymbol(LabeledStatementSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a switch label syntax, get the corresponding label symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node of the switch label.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The label symbol for that label.</returns>
        public abstract ILabelSymbol GetDeclaredSymbol(SwitchLabelSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a using declaration get the corresponding symbol for the using alias that was
        /// introduced.
        /// </summary>
        /// <param name="declarationSyntax"></param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The alias symbol that was declared.</returns>
        /// <remarks>
        /// If the using directive is an error because it attempts to introduce an alias for which an existing alias was
        /// previously declared in the same scope, the result is a newly-constructed AliasSymbol (i.e. not one from the
        /// symbol table).
        /// </remarks>
        public abstract IAliasSymbol GetDeclaredSymbol(UsingDirectiveSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given an extern alias declaration get the corresponding symbol for the alias that was introduced.
        /// </summary>
        /// <param name="declarationSyntax"></param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The alias symbol that was declared, or null if a duplicate alias symbol was declared.</returns>
        public abstract IAliasSymbol GetDeclaredSymbol(ExternAliasDirectiveSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a parameter declaration syntax node, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a parameter.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The parameter that was declared.</returns>
        public abstract IParameterSymbol GetDeclaredSymbol(ParameterSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Given a base field declaration syntax, get the corresponding symbols.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares one or more fields or events.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbols that were declared.</returns>
        internal abstract ImmutableArray<ISymbol> GetDeclaredSymbols(BaseFieldDeclarationSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken));

        protected ParameterSymbol GetParameterSymbol(
            ImmutableArray<ParameterSymbol> parameters,
            ParameterSyntax parameter,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            foreach (var symbol in parameters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var location in symbol.Locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (location.SourceTree == this.SyntaxTree && parameter.Span.Contains(location.SourceSpan))
                    {
                        return symbol;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Given a type parameter declaration (field or method), get the corresponding symbol
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="typeParameter"></param>
        public abstract ITypeParameterSymbol GetDeclaredSymbol(TypeParameterSyntax typeParameter, CancellationToken cancellationToken = default(CancellationToken));

        internal BinderFlags GetSemanticModelBinderFlags()
        {
            return this.IgnoresAccessibility
                ? BinderFlags.SemanticModel | BinderFlags.IgnoreAccessibility
                : BinderFlags.SemanticModel;
        }

        /// <summary>
        /// Given a foreach statement, get the symbol for the iteration variable
        /// </summary>
        /// <param name="forEachStatement"></param>
        public ILocalSymbol GetDeclaredSymbol(ForEachStatementSyntax forEachStatement)
        {
            Binder enclosingBinder = this.GetEnclosingBinder(GetAdjustedNodePosition(forEachStatement));

            if (enclosingBinder == null)
            {
                return null;
            }

            Binder foreachBinder = enclosingBinder.GetBinder(forEachStatement);

            // Binder.GetBinder can fail in presence of syntax errors. 
            if (foreachBinder == null)
            {
                return null;
            }

            LocalSymbol local = foreachBinder.GetDeclaredLocalsForScope(forEachStatement).FirstOrDefault();
            return (local is SourceLocalSymbol { DeclarationKind: LocalDeclarationKind.ForEachIterationVariable } sourceLocal
                ? GetAdjustedLocalSymbol(sourceLocal)
                : local).GetPublicSymbol();
        }

        /// <summary>
        /// Given a local symbol, gets an updated version of that local symbol adjusted for nullability analysis
        /// if the analysis affects the local.
        /// </summary>
        /// <param name="originalSymbol">The original symbol from initial binding.</param>
        /// 
        /// <returns>The nullability-adjusted local, or the original symbol if the nullability analysis made no adjustments or was not run.</returns>
        internal abstract LocalSymbol GetAdjustedLocalSymbol(SourceLocalSymbol originalSymbol);

        /// <summary>
        /// Given a catch declaration, get the symbol for the exception variable
        /// </summary>
        /// <param name="catchDeclaration"></param>
        public ILocalSymbol GetDeclaredSymbol(CatchDeclarationSyntax catchDeclaration)
        {
            CSharpSyntaxNode catchClause = catchDeclaration.Parent; //Syntax->Binder map is keyed on clause, not decl
            Debug.Assert(catchClause.Kind() == SyntaxKind.CatchClause);
            Binder enclosingBinder = this.GetEnclosingBinder(GetAdjustedNodePosition(catchClause));

            if (enclosingBinder == null)
            {
                return null;
            }

            Binder catchBinder = enclosingBinder.GetBinder(catchClause);

            // Binder.GetBinder can fail in presence of syntax errors. 
            if (catchBinder == null)
            {
                return null;
            }

            catchBinder = enclosingBinder.GetBinder(catchClause);
            LocalSymbol local = catchBinder.GetDeclaredLocalsForScope(catchClause).FirstOrDefault();
            return ((object)local != null && local.DeclarationKind == LocalDeclarationKind.CatchVariable)
                ? local.GetPublicSymbol()
                : null;
        }

        public abstract IRangeVariableSymbol GetDeclaredSymbol(QueryClauseSyntax queryClause, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Get the query range variable declared in a join into clause.
        /// </summary>
        public abstract IRangeVariableSymbol GetDeclaredSymbol(JoinIntoClauseSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Get the query range variable declared in a query continuation clause.
        /// </summary>
        public abstract IRangeVariableSymbol GetDeclaredSymbol(QueryContinuationSyntax node, CancellationToken cancellationToken = default(CancellationToken));

        // Get the symbols and possible method or property group associated with a bound node, as
        // they should be exposed through GetSemanticInfo.
        // NB: It is not safe to pass a null binderOpt during speculative binding.
        private OneOrMany<Symbol> GetSemanticSymbols(
            BoundExpression boundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt,
            SymbolInfoOptions options,
            out bool isDynamic,
            out LookupResultKind resultKind,
            out ImmutableArray<Symbol> memberGroup)
        {
            memberGroup = ImmutableArray<Symbol>.Empty;
            OneOrMany<Symbol> symbols = OneOrMany<Symbol>.Empty;
            resultKind = LookupResultKind.Viable;
            isDynamic = false;

            switch (boundNode.Kind)
            {
                case BoundKind.MethodGroup:
                    symbols = GetMethodGroupSemanticSymbols((BoundMethodGroup)boundNode, boundNodeForSyntacticParent, binderOpt, out resultKind, out isDynamic, out memberGroup);
                    break;

                case BoundKind.PropertyGroup:
                    symbols = GetPropertyGroupSemanticSymbols((BoundPropertyGroup)boundNode, boundNodeForSyntacticParent, binderOpt, out resultKind, out memberGroup);
                    break;
                // Tracked by https://github.com/dotnet/roslyn/issues/78957 : public API, consider handling BoundPropertyAccess (which now may have a member group)

                case BoundKind.BadExpression:
                    {
                        var expr = (BoundBadExpression)boundNode;
                        resultKind = expr.ResultKind;

                        if (expr.Syntax.Kind() is SyntaxKind.ObjectCreationExpression or SyntaxKind.ImplicitObjectCreationExpression)
                        {
                            if (resultKind == LookupResultKind.NotCreatable)
                            {
                                return OneOrMany.Create(expr.Symbols);
                            }
                            else if (expr.Type.IsDelegateType())
                            {
                                resultKind = LookupResultKind.Empty;
                                return symbols;
                            }

                            memberGroup = expr.Symbols;
                        }

                        return OneOrMany.Create(expr.Symbols);
                    }

                case BoundKind.DelegateCreationExpression:
                    break;

                case BoundKind.TypeExpression:
                    {
                        var boundType = (BoundTypeExpression)boundNode;

                        // Watch out for not creatable types within object creation syntax
                        if (boundNodeForSyntacticParent != null &&
                           boundNodeForSyntacticParent.Syntax.Kind() == SyntaxKind.ObjectCreationExpression &&
                           ((ObjectCreationExpressionSyntax)boundNodeForSyntacticParent.Syntax).Type == boundType.Syntax &&
                           boundNodeForSyntacticParent.Kind == BoundKind.BadExpression &&
                           ((BoundBadExpression)boundNodeForSyntacticParent).ResultKind == LookupResultKind.NotCreatable)
                        {
                            resultKind = LookupResultKind.NotCreatable;
                        }

                        // could be a type or alias.
                        var typeSymbol = boundType.AliasOpt ?? (Symbol)boundType.Type;

                        var originalErrorType = typeSymbol.OriginalDefinition as ErrorTypeSymbol;
                        if ((object)originalErrorType != null)
                        {
                            resultKind = originalErrorType.ResultKind;
                            symbols = OneOrMany.Create(originalErrorType.CandidateSymbols);
                        }
                        else
                        {
                            symbols = OneOrMany.Create(typeSymbol);
                        }
                    }
                    break;

                case BoundKind.TypeOrValueExpression:
                    {
                        // If we're seeing a node of this kind, then we failed to resolve the member access
                        // as either a type or a property/field/event/local/parameter.  In such cases,
                        // the second interpretation applies so just visit the node for that.
                        BoundExpression valueExpression = ((BoundTypeOrValueExpression)boundNode).Data.ValueExpression;
                        return GetSemanticSymbols(valueExpression, boundNodeForSyntacticParent, binderOpt, options, out isDynamic, out resultKind, out memberGroup);
                    }

                case BoundKind.Call:
                    {
                        // Either overload resolution succeeded for this call or it did not. If it
                        // did not succeed then we've stashed the original method symbols from the
                        // method group, and we should use those as the symbols displayed for the
                        // call. If it did succeed then we did not stash any symbols; just fall
                        // through to the default case.

                        var call = (BoundCall)boundNode;
                        if (call.OriginalMethodsOpt.IsDefault)
                        {
                            if ((object)call.Method != null)
                            {
                                symbols = CreateReducedExtensionMethodIfPossible(call);
                                resultKind = call.ResultKind;
                            }
                        }
                        else
                        {
                            symbols = CreateReducedAndFilteredSymbolsFromOriginals(call, Compilation);
                            resultKind = call.ResultKind;
                        }
                    }
                    break;

                case BoundKind.FunctionPointerInvocation:
                    {
                        var invocation = (BoundFunctionPointerInvocation)boundNode;
                        symbols = OneOrMany.Create<Symbol>(invocation.FunctionPointer);
                        resultKind = invocation.ResultKind;
                        break;
                    }

                case BoundKind.UnconvertedAddressOfOperator:
                    {
                        // We try to match the results given for a similar piece of syntax here: bad invocations.
                        // A BoundUnconvertedAddressOfOperator represents this syntax: &M
                        // Similarly, a BoundCall for a bad invocation represents this syntax: M(args)
                        // Calling GetSymbolInfo on the syntax will return an array of candidate symbols that were
                        // looked up, but calling GetMemberGroup will return an empty array. So, we ignore the member
                        // group result in the call below.
                        symbols = GetMethodGroupSemanticSymbols(
                            ((BoundUnconvertedAddressOfOperator)boundNode).Operand,
                            boundNodeForSyntacticParent, binderOpt, out resultKind, out isDynamic, memberGroup: out _);
                        break;
                    }

                case BoundKind.IndexerAccess:
                    {
                        // As for BoundCall, pull out stashed candidates if overload resolution failed.

                        BoundIndexerAccess indexerAccess = (BoundIndexerAccess)boundNode;
                        Debug.Assert((object)indexerAccess.Indexer != null);

                        resultKind = indexerAccess.ResultKind;

                        ImmutableArray<PropertySymbol> originalIndexersOpt = indexerAccess.OriginalIndexersOpt;
                        symbols = originalIndexersOpt.IsDefault ? OneOrMany.Create<Symbol>(indexerAccess.Indexer) : StaticCast<Symbol>.From(OneOrMany.Create(originalIndexersOpt));
                    }
                    break;

                case BoundKind.ImplicitIndexerAccess:
                    return GetSemanticSymbols(((BoundImplicitIndexerAccess)boundNode).IndexerOrSliceAccess,
                        boundNodeForSyntacticParent, binderOpt, options, out isDynamic, out resultKind, out memberGroup);

                case BoundKind.EventAssignmentOperator:
                    var eventAssignment = (BoundEventAssignmentOperator)boundNode;
                    isDynamic = eventAssignment.IsDynamic;
                    var eventSymbol = eventAssignment.Event;
                    var methodSymbol = eventAssignment.IsAddition ? eventSymbol.AddMethod : eventSymbol.RemoveMethod;
                    if ((object)methodSymbol == null)
                    {
                        symbols = OneOrMany<Symbol>.Empty;
                        resultKind = LookupResultKind.Empty;
                    }
                    else
                    {
                        symbols = OneOrMany.Create<Symbol>(methodSymbol);
                        resultKind = eventAssignment.ResultKind;
                    }
                    break;

                case BoundKind.EventAccess when boundNodeForSyntacticParent is BoundEventAssignmentOperator { ResultKind: LookupResultKind.Viable } parentOperator &&
                                                boundNode.ExpressionSymbol is Symbol accessSymbol &&
                                                boundNode != parentOperator.Argument &&
                                                parentOperator.Event.Equals(accessSymbol, TypeCompareKind.AllNullableIgnoreOptions):
                    // When we're looking at the left-hand side of an event assignment, we synthesize a BoundEventAccess node. This node does not have
                    // nullability information, however, so if we're in that case then we need to grab the event symbol from the parent event assignment
                    // which does have the nullability-reinferred symbol
                    symbols = OneOrMany.Create<Symbol>(parentOperator.Event);
                    resultKind = parentOperator.ResultKind;
                    break;

                case BoundKind.Conversion:
                    var conversion = (BoundConversion)boundNode;
                    isDynamic = conversion.ConversionKind.IsDynamic();
                    if (!isDynamic)
                    {
                        if ((conversion.ConversionKind == ConversionKind.MethodGroup) && conversion.IsExtensionMethod)
                        {
                            var symbol = conversion.SymbolOpt;
                            Debug.Assert((object)symbol != null);
                            symbols = OneOrMany.Create<Symbol>(ReducedExtensionMethodSymbol.Create(symbol));
                            resultKind = conversion.ResultKind;
                        }
                        else if (conversion.ConversionKind.IsUserDefinedConversion())
                        {
                            GetSymbolsAndResultKind(conversion, conversion.SymbolOpt, conversion.Conversion.OriginalUserDefinedConversions, out symbols, out resultKind);
                        }
                        else
                        {
                            goto default;
                        }
                    }
                    break;

                case BoundKind.BinaryOperator:
                    GetSymbolsAndResultKind((BoundBinaryOperator)boundNode, out isDynamic, ref resultKind, ref symbols);
                    break;

                case BoundKind.UnaryOperator:
                    GetSymbolsAndResultKind((BoundUnaryOperator)boundNode, out isDynamic, ref resultKind, ref symbols);
                    break;

                case BoundKind.UserDefinedConditionalLogicalOperator:
                    var @operator = (BoundUserDefinedConditionalLogicalOperator)boundNode;
                    isDynamic = false;
                    GetSymbolsAndResultKind(@operator, @operator.LogicalOperator, @operator.OriginalUserDefinedOperatorsOpt, out symbols, out resultKind);
                    break;

                case BoundKind.CompoundAssignmentOperator:
                    GetSymbolsAndResultKind((BoundCompoundAssignmentOperator)boundNode, out isDynamic, ref resultKind, ref symbols);
                    break;

                case BoundKind.IncrementOperator:
                    GetSymbolsAndResultKind((BoundIncrementOperator)boundNode, out isDynamic, ref resultKind, ref symbols);
                    break;

                case BoundKind.AwaitExpression:
                    var await = (BoundAwaitExpression)boundNode;
                    isDynamic = await.AwaitableInfo.IsDynamic;
                    goto default;

                case BoundKind.ConditionalOperator:
                    var conditional = (BoundConditionalOperator)boundNode;
                    Debug.Assert(conditional.ExpressionSymbol is null);
                    isDynamic = conditional.IsDynamic;
                    goto default;

                case BoundKind.Attribute:
                    {
                        Debug.Assert(boundNodeForSyntacticParent == null);
                        var attribute = (BoundAttribute)boundNode;
                        resultKind = attribute.ResultKind;

                        // If attribute name bound to a single named type or an error type
                        // with a single named type candidate symbol, we will return constructors
                        // of the named type in the semantic info.
                        // Otherwise, we will return the error type candidate symbols.

                        var namedType = (NamedTypeSymbol)attribute.Type;
                        if (namedType.IsErrorType())
                        {
                            Debug.Assert(resultKind != LookupResultKind.Viable);
                            var errorType = (ErrorTypeSymbol)namedType;
                            var candidateSymbols = errorType.CandidateSymbols;

                            // If error type has a single named type candidate symbol, we want to 
                            // use that type for symbol info. 
                            if (candidateSymbols.Length == 1 && candidateSymbols[0] is NamedTypeSymbol)
                            {
                                namedType = (NamedTypeSymbol)candidateSymbols[0];
                            }
                            else
                            {
                                symbols = OneOrMany.Create(candidateSymbols);
                                break;
                            }
                        }

                        AdjustSymbolsForObjectCreation(attribute, namedType, attribute.Constructor, binderOpt, ref resultKind, ref symbols, ref memberGroup);
                    }
                    break;

                case BoundKind.QueryClause:
                    {
                        var query = (BoundQueryClause)boundNode;
                        var builder = ArrayBuilder<Symbol>.GetInstance();
                        if (query.Operation != null && (object)query.Operation.ExpressionSymbol != null) builder.Add(query.Operation.ExpressionSymbol);
                        if ((object)query.DefinedSymbol != null) builder.Add(query.DefinedSymbol);
                        if (query.Cast != null && (object)query.Cast.ExpressionSymbol != null) builder.Add(query.Cast.ExpressionSymbol);
                        symbols = builder.ToOneOrManyAndFree();
                    }
                    break;

                case BoundKind.DynamicInvocation:
                    var dynamicInvocation = (BoundDynamicInvocation)boundNode;
                    Debug.Assert(dynamicInvocation.ExpressionSymbol is null);
                    memberGroup = dynamicInvocation.ApplicableMethods.Cast<MethodSymbol, Symbol>();
                    symbols = OneOrMany.Create(memberGroup);
                    isDynamic = true;
                    break;

                case BoundKind.DynamicCollectionElementInitializer:
                    var collectionInit = (BoundDynamicCollectionElementInitializer)boundNode;
                    Debug.Assert(collectionInit.ExpressionSymbol is null);
                    memberGroup = collectionInit.ApplicableMethods.Cast<MethodSymbol, Symbol>();
                    symbols = OneOrMany.Create(memberGroup);
                    isDynamic = true;
                    break;

                case BoundKind.DynamicIndexerAccess:
                    var dynamicIndexer = (BoundDynamicIndexerAccess)boundNode;
                    Debug.Assert(dynamicIndexer.ExpressionSymbol is null);
                    memberGroup = dynamicIndexer.ApplicableIndexers.Cast<PropertySymbol, Symbol>();
                    symbols = OneOrMany.Create(memberGroup);
                    isDynamic = true;
                    break;

                case BoundKind.DynamicMemberAccess:
                    Debug.Assert((object)boundNode.ExpressionSymbol == null);
                    isDynamic = true;
                    break;

                case BoundKind.DynamicObjectCreationExpression:
                    var objectCreation = (BoundDynamicObjectCreationExpression)boundNode;
                    memberGroup = objectCreation.ApplicableMethods.Cast<MethodSymbol, Symbol>();
                    symbols = OneOrMany.Create(memberGroup);
                    isDynamic = true;
                    break;

                case BoundKind.ObjectCreationExpression:
                    var boundObjectCreation = (BoundObjectCreationExpression)boundNode;

                    if ((object)boundObjectCreation.Constructor != null)
                    {
                        Debug.Assert(boundObjectCreation.ConstructorsGroup.Contains(boundObjectCreation.Constructor));
                        symbols = OneOrMany.Create<Symbol>(boundObjectCreation.Constructor);
                    }
                    else if (boundObjectCreation.ConstructorsGroup.Length > 0)
                    {
                        symbols = StaticCast<Symbol>.From(OneOrMany.Create(boundObjectCreation.ConstructorsGroup));
                        resultKind = resultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                    }

                    memberGroup = boundObjectCreation.ConstructorsGroup.Cast<MethodSymbol, Symbol>();
                    break;

                case BoundKind.ThisReference:
                case BoundKind.BaseReference:
                    {
                        Binder binder = binderOpt ?? GetEnclosingBinder(GetAdjustedNodePosition(boundNode.Syntax));
                        NamedTypeSymbol containingType = binder.ContainingType;
                        var containingMember = binder.ContainingMember();

                        var thisParam = GetThisParameter(boundNode.Type, containingType, containingMember, out resultKind);
                        symbols = thisParam != null ? OneOrMany.Create<Symbol>(thisParam) : OneOrMany<Symbol>.Empty;
                    }
                    break;

                case BoundKind.FromEndIndexExpression:
                    {
                        var fromEndIndexExpression = (BoundFromEndIndexExpression)boundNode;
                        if ((object)fromEndIndexExpression.MethodOpt != null)
                        {
                            symbols = OneOrMany.Create<Symbol>(fromEndIndexExpression.MethodOpt);
                        }
                        break;
                    }

                case BoundKind.RangeExpression:
                    {
                        var rangeExpression = (BoundRangeExpression)boundNode;
                        if ((object)rangeExpression.MethodOpt != null)
                        {
                            symbols = OneOrMany.Create<Symbol>(rangeExpression.MethodOpt);
                        }
                        break;
                    }

                default:
                    {
                        if (boundNode.ExpressionSymbol is Symbol symbol)
                        {
                            symbols = OneOrMany.Create(symbol);
                            resultKind = boundNode.ResultKind;
                        }
                    }
                    break;
            }

            if (boundNodeForSyntacticParent != null && (options & SymbolInfoOptions.PreferConstructorsToType) != 0)
            {
                // Adjust symbols to get the constructors if we're T in a "new T(...)".
                AdjustSymbolsForObjectCreation(boundNode, boundNodeForSyntacticParent, binderOpt, ref resultKind, ref symbols, ref memberGroup);
            }

            return symbols;
        }

        private static ParameterSymbol GetThisParameter(TypeSymbol typeOfThis, NamedTypeSymbol containingType, Symbol containingMember, out LookupResultKind resultKind)
        {
            if ((object)containingMember == null || (object)containingType == null)
            {
                // not in a member of a type (can happen when speculating)
                resultKind = LookupResultKind.NotReferencable;
                return new ThisParameterSymbol(containingMember as MethodSymbol, typeOfThis);
            }

            ParameterSymbol thisParam;

            switch (containingMember.Kind)
            {
                case SymbolKind.Method:
                case SymbolKind.Field:
                case SymbolKind.Property:
                    if (containingMember.IsStatic)
                    {
                        // in a static member
                        resultKind = LookupResultKind.StaticInstanceMismatch;
                        thisParam = new ThisParameterSymbol(containingMember as MethodSymbol, containingType);
                    }
                    else
                    {
                        if ((object)typeOfThis == ErrorTypeSymbol.UnknownResultType)
                        {
                            // in an instance member, but binder considered this/base unreferenceable
                            thisParam = new ThisParameterSymbol(containingMember as MethodSymbol, containingType);
                            resultKind = LookupResultKind.NotReferencable;
                        }
                        else
                        {
                            switch (containingMember.Kind)
                            {
                                case SymbolKind.Method:
                                    resultKind = LookupResultKind.Viable;
                                    thisParam = containingMember.EnclosingThisSymbol();
                                    break;

                                // Fields and properties can't access 'this' since
                                // initializers are run in the constructor    
                                case SymbolKind.Field:
                                case SymbolKind.Property:
                                    resultKind = LookupResultKind.NotReferencable;
                                    thisParam = containingMember.EnclosingThisSymbol() ?? new ThisParameterSymbol(null, containingType);
                                    break;

                                default:
                                    throw ExceptionUtilities.UnexpectedValue(containingMember.Kind);
                            }
                        }
                    }
                    break;

                default:
                    thisParam = new ThisParameterSymbol(containingMember as MethodSymbol, typeOfThis);
                    resultKind = LookupResultKind.NotReferencable;
                    break;
            }

            return thisParam;
        }

        private static void GetSymbolsAndResultKind(BoundUnaryOperator unaryOperator, out bool isDynamic, ref LookupResultKind resultKind, ref OneOrMany<Symbol> symbols)
        {
            UnaryOperatorKind operandType = unaryOperator.OperatorKind.OperandTypes();
            isDynamic = unaryOperator.OperatorKind.IsDynamic();

            if (operandType == 0 || operandType == UnaryOperatorKind.UserDefined || unaryOperator.ResultKind != LookupResultKind.Viable)
            {
                if (!isDynamic)
                {
                    GetSymbolsAndResultKind(unaryOperator, unaryOperator.MethodOpt, unaryOperator.OriginalUserDefinedOperatorsOpt, out symbols, out resultKind);
                }
            }
            else
            {
                Debug.Assert((object)unaryOperator.MethodOpt == null && unaryOperator.OriginalUserDefinedOperatorsOpt.IsDefaultOrEmpty);
                UnaryOperatorKind op = unaryOperator.OperatorKind.Operator();
                symbols = OneOrMany.Create<Symbol>(new SynthesizedIntrinsicOperatorSymbol(unaryOperator.Operand.Type.StrippedType(),
                                                                                          OperatorFacts.UnaryOperatorNameFromOperatorKind(op, isChecked: unaryOperator.OperatorKind.IsChecked()),
                                                                                          unaryOperator.Type.StrippedType()));
                resultKind = unaryOperator.ResultKind;
            }
        }

        private static void GetSymbolsAndResultKind(BoundIncrementOperator increment, out bool isDynamic, ref LookupResultKind resultKind, ref OneOrMany<Symbol> symbols)
        {
            UnaryOperatorKind operandType = increment.OperatorKind.OperandTypes();
            isDynamic = increment.OperatorKind.IsDynamic();

            if (operandType == 0 || operandType == UnaryOperatorKind.UserDefined || increment.ResultKind != LookupResultKind.Viable)
            {
                if (!isDynamic)
                {
                    GetSymbolsAndResultKind(increment, increment.MethodOpt, increment.OriginalUserDefinedOperatorsOpt, out symbols, out resultKind);
                }
            }
            else
            {
                Debug.Assert((object)increment.MethodOpt == null && increment.OriginalUserDefinedOperatorsOpt.IsDefaultOrEmpty);
                UnaryOperatorKind op = increment.OperatorKind.Operator();
                TypeSymbol opType = increment.Operand.Type.StrippedType();
                symbols = OneOrMany.Create<Symbol>(new SynthesizedIntrinsicOperatorSymbol(opType,
                                                                                          OperatorFacts.UnaryOperatorNameFromOperatorKind(op, isChecked: increment.OperatorKind.IsChecked()),
                                                                                          opType));
                resultKind = increment.ResultKind;
            }
        }

        private static void GetSymbolsAndResultKind(BoundBinaryOperator binaryOperator, out bool isDynamic, ref LookupResultKind resultKind, ref OneOrMany<Symbol> symbols)
        {
            BinaryOperatorKind operandType = binaryOperator.OperatorKind.OperandTypes();
            BinaryOperatorKind op = binaryOperator.OperatorKind.Operator();
            isDynamic = binaryOperator.OperatorKind.IsDynamic();

            if (operandType == 0 || operandType == BinaryOperatorKind.UserDefined || binaryOperator.ResultKind != LookupResultKind.Viable || binaryOperator.OperatorKind.IsLogical())
            {
                if (!isDynamic)
                {
                    GetSymbolsAndResultKind(binaryOperator, binaryOperator.Method, binaryOperator.OriginalUserDefinedOperatorsOpt, out symbols, out resultKind);
                }
            }
            else
            {
                Debug.Assert((object)binaryOperator.Method == null && binaryOperator.OriginalUserDefinedOperatorsOpt.IsDefaultOrEmpty);

                if (!isDynamic &&
                    (op == BinaryOperatorKind.Equal || op == BinaryOperatorKind.NotEqual) &&
                    ((binaryOperator.Left.IsLiteralNull() && binaryOperator.Right.Type.IsNullableType()) ||
                     (binaryOperator.Right.IsLiteralNull() && binaryOperator.Left.Type.IsNullableType())) &&
                    binaryOperator.Type.SpecialType == SpecialType.System_Boolean)
                {
                    // Comparison of a nullable type with null, return corresponding operator for Object.
                    var objectType = binaryOperator.Type.ContainingAssembly.GetSpecialType(SpecialType.System_Object);

                    symbols = OneOrMany.Create<Symbol>(new SynthesizedIntrinsicOperatorSymbol(objectType,
                                                                                              OperatorFacts.BinaryOperatorNameFromOperatorKind(op, isChecked: binaryOperator.OperatorKind.IsChecked()),
                                                                                              objectType,
                                                                                              binaryOperator.Type));
                }
                else
                {
                    symbols = OneOrMany.Create(GetIntrinsicOperatorSymbol(op, isDynamic,
                                                                          binaryOperator.Left.Type,
                                                                          binaryOperator.Right.Type,
                                                                          binaryOperator.Type,
                                                                          binaryOperator.OperatorKind.IsChecked()));
                }

                resultKind = binaryOperator.ResultKind;
            }
        }

        private static Symbol GetIntrinsicOperatorSymbol(BinaryOperatorKind op, bool isDynamic, TypeSymbol leftType, TypeSymbol rightType, TypeSymbol returnType, bool isChecked)
        {
            if (!isDynamic)
            {
                leftType = leftType.StrippedType();
                rightType = rightType.StrippedType();
                returnType = returnType.StrippedType();
            }
            else
            {
                Debug.Assert(returnType.IsDynamic());

                if ((object)leftType == null)
                {
                    Debug.Assert(rightType.IsDynamic());
                    leftType = rightType;
                }
                else if ((object)rightType == null)
                {
                    Debug.Assert(leftType.IsDynamic());
                    rightType = leftType;
                }
            }
            return new SynthesizedIntrinsicOperatorSymbol(leftType,
                                                          OperatorFacts.BinaryOperatorNameFromOperatorKind(op, isChecked),
                                                          rightType,
                                                          returnType);
        }

        private static void GetSymbolsAndResultKind(BoundCompoundAssignmentOperator compoundAssignment, out bool isDynamic, ref LookupResultKind resultKind, ref OneOrMany<Symbol> symbols)
        {
            BinaryOperatorKind operandType = compoundAssignment.Operator.Kind.OperandTypes();
            BinaryOperatorKind op = compoundAssignment.Operator.Kind.Operator();
            isDynamic = compoundAssignment.Operator.Kind.IsDynamic();

            if (operandType == 0 || operandType == BinaryOperatorKind.UserDefined || compoundAssignment.ResultKind != LookupResultKind.Viable)
            {
                if (!isDynamic)
                {
                    GetSymbolsAndResultKind(compoundAssignment, compoundAssignment.Operator.Method, compoundAssignment.OriginalUserDefinedOperatorsOpt, out symbols, out resultKind);
                }
            }
            else
            {
                Debug.Assert((object)compoundAssignment.Operator.Method == null && compoundAssignment.OriginalUserDefinedOperatorsOpt.IsDefaultOrEmpty);

                symbols = OneOrMany.Create(GetIntrinsicOperatorSymbol(op, isDynamic,
                                                                      compoundAssignment.Operator.LeftType,
                                                                      compoundAssignment.Operator.RightType,
                                                                      compoundAssignment.Operator.ReturnType,
                                                                      compoundAssignment.Operator.Kind.IsChecked()));
                resultKind = compoundAssignment.ResultKind;
            }
        }

        private static void GetSymbolsAndResultKind(BoundExpression node, Symbol symbolOpt, ImmutableArray<MethodSymbol> originalCandidates, out OneOrMany<Symbol> symbols, out LookupResultKind resultKind)
        {
            if (!ReferenceEquals(symbolOpt, null))
            {
                symbols = OneOrMany.Create(symbolOpt);
                resultKind = node.ResultKind;
            }
            else if (!originalCandidates.IsDefault)
            {
                symbols = StaticCast<Symbol>.From(OneOrMany.Create(originalCandidates));
                resultKind = node.ResultKind;
            }
            else
            {
                symbols = OneOrMany<Symbol>.Empty;
                resultKind = LookupResultKind.Empty;
            }
        }

        // In cases where we are binding C in "[C(...)]", the bound nodes return the symbol for the type. However, we've
        // decided that we want this case to return the constructor of the type instead. This affects attributes. 
        // This method checks for this situation and adjusts the syntax and method or property group.
        private void AdjustSymbolsForObjectCreation(
            BoundExpression boundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt,
            ref LookupResultKind resultKind,
            ref OneOrMany<Symbol> symbols,
            ref ImmutableArray<Symbol> memberGroup)
        {
            NamedTypeSymbol typeSymbol = null;
            MethodSymbol constructor = null;

            // Check if boundNode.Syntax is the type-name child of an Attribute.
            SyntaxNode parentSyntax = boundNodeForSyntacticParent.Syntax;
            if (parentSyntax != null &&
                parentSyntax == boundNode.Syntax.Parent &&
                parentSyntax.Kind() == SyntaxKind.Attribute && ((AttributeSyntax)parentSyntax).Name == boundNode.Syntax)
            {
                var unwrappedSymbols = UnwrapAliases(symbols);

                switch (boundNodeForSyntacticParent.Kind)
                {
                    case BoundKind.Attribute:
                        BoundAttribute boundAttribute = (BoundAttribute)boundNodeForSyntacticParent;

                        if (unwrappedSymbols.Count == 1 && unwrappedSymbols[0].Kind == SymbolKind.NamedType)
                        {
                            Debug.Assert(resultKind != LookupResultKind.Viable ||
                                TypeSymbol.Equals((TypeSymbol)unwrappedSymbols[0], boundAttribute.Type.GetNonErrorGuess(), TypeCompareKind.ConsiderEverything2));

                            typeSymbol = (NamedTypeSymbol)unwrappedSymbols[0];
                            constructor = boundAttribute.Constructor;
                            resultKind = resultKind.WorseResultKind(boundAttribute.ResultKind);
                        }
                        break;

                    case BoundKind.BadExpression:
                        BoundBadExpression boundBadExpression = (BoundBadExpression)boundNodeForSyntacticParent;
                        if (unwrappedSymbols.Count == 1)
                        {
                            resultKind = resultKind.WorseResultKind(boundBadExpression.ResultKind);
                            typeSymbol = unwrappedSymbols[0] as NamedTypeSymbol;
                        }
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(boundNodeForSyntacticParent.Kind);
                }

                AdjustSymbolsForObjectCreation(boundNode, typeSymbol, constructor, binderOpt, ref resultKind, ref symbols, ref memberGroup);
            }
        }

        private void AdjustSymbolsForObjectCreation(
            BoundNode lowestBoundNode,
            NamedTypeSymbol typeSymbolOpt,
            MethodSymbol constructorOpt,
            Binder binderOpt,
            ref LookupResultKind resultKind,
            ref OneOrMany<Symbol> symbols,
            ref ImmutableArray<Symbol> memberGroup)
        {
            Debug.Assert(lowestBoundNode != null);
            Debug.Assert(binderOpt != null || IsInTree(lowestBoundNode.Syntax));

            if ((object)typeSymbolOpt != null)
            {
                Debug.Assert(lowestBoundNode.Syntax != null);

                // Filter typeSymbol's instance constructors by accessibility.
                // If all the instance constructors are inaccessible, we retain
                // all of them for correct semantic info.
                Binder binder = binderOpt ?? GetEnclosingBinder(GetAdjustedNodePosition(lowestBoundNode.Syntax));
                ImmutableArray<MethodSymbol> candidateConstructors;

                if (binder != null)
                {
                    var instanceConstructors = typeSymbolOpt.IsInterfaceType() && (object)typeSymbolOpt.ComImportCoClass != null ?
                        typeSymbolOpt.ComImportCoClass.InstanceConstructors :
                        typeSymbolOpt.InstanceConstructors;

                    var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                    candidateConstructors = binder.FilterInaccessibleConstructors(instanceConstructors, allowProtectedConstructorsOfBaseType: false, useSiteInfo: ref discardedUseSiteInfo);

                    if ((object)constructorOpt == null ? !candidateConstructors.Any() : !candidateConstructors.Contains(constructorOpt))
                    {
                        // All instance constructors are inaccessible or if the specified constructor
                        // isn't a candidate, then we retain all of them for correct semantic info.
                        Debug.Assert(resultKind != LookupResultKind.Viable);
                        candidateConstructors = instanceConstructors;
                    }
                }
                else
                {
                    candidateConstructors = ImmutableArray<MethodSymbol>.Empty;
                }

                if ((object)constructorOpt != null)
                {
                    Debug.Assert(candidateConstructors.Contains(constructorOpt));
                    symbols = OneOrMany.Create<Symbol>(constructorOpt);
                }
                else if (candidateConstructors.Length > 0)
                {
                    symbols = StaticCast<Symbol>.From(OneOrMany.Create(candidateConstructors));
                    Debug.Assert(resultKind != LookupResultKind.Viable);
                    resultKind = resultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                }

                memberGroup = candidateConstructors.Cast<MethodSymbol, Symbol>();
            }
        }

        /// <summary>
        /// Returns a list of accessible, non-hidden indexers that could be invoked with the given expression
        /// as a receiver.
        /// </summary>
        /// <remarks>
        /// If the given expression is an indexer access, then this method will return the list of indexers
        /// that could be invoked on the result, not the list of indexers that were considered.
        /// </remarks>
        private ImmutableArray<IPropertySymbol> GetIndexerGroupSemanticSymbols(BoundExpression boundNode, Binder binderOpt)
        {
            Debug.Assert(binderOpt != null || IsInTree(boundNode.Syntax));

            TypeSymbol type = boundNode.Type;

            if (ReferenceEquals(type, null) || type.IsStatic)
            {
                return ImmutableArray<IPropertySymbol>.Empty;
            }

            Binder binder = binderOpt ?? GetEnclosingBinder(GetAdjustedNodePosition(boundNode.Syntax));
            var symbols = ArrayBuilder<ISymbol>.GetInstance();
            AppendSymbolsWithNameAndArity(symbols, WellKnownMemberNames.Indexer, 0, binder, type, LookupOptions.MustBeInstance);

            if (symbols.Count == 0)
            {
                symbols.Free();
                return ImmutableArray<IPropertySymbol>.Empty;
            }

            var result = FilterOverriddenOrHiddenIndexers(symbols);
            symbols.Free();

            return result;
        }

        private static ImmutableArray<IPropertySymbol> FilterOverriddenOrHiddenIndexers(ArrayBuilder<ISymbol> symbols)
        {
            PooledHashSet<Symbol> hiddenSymbols = null;
            foreach (ISymbol iSymbol in symbols)
            {
                Symbol symbol = iSymbol.GetSymbol();
                Debug.Assert(symbol.IsIndexer(), "Only indexers can have name " + WellKnownMemberNames.Indexer);

                PropertySymbol indexer = (PropertySymbol)symbol;
                OverriddenOrHiddenMembersResult overriddenOrHiddenMembers = indexer.OverriddenOrHiddenMembers;

                foreach (Symbol overridden in overriddenOrHiddenMembers.OverriddenMembers)
                {
                    if (hiddenSymbols == null)
                    {
                        hiddenSymbols = PooledHashSet<Symbol>.GetInstance();
                    }
                    hiddenSymbols.Add(overridden);
                }

                // Don't worry about RuntimeOverriddenMembers - this check is for the API, which
                // should reflect the C# semantics.

                foreach (Symbol hidden in overriddenOrHiddenMembers.HiddenMembers)
                {
                    if (hiddenSymbols == null)
                    {
                        hiddenSymbols = PooledHashSet<Symbol>.GetInstance();
                    }
                    hiddenSymbols.Add(hidden);
                }
            }

            var builder = ArrayBuilder<IPropertySymbol>.GetInstance();

            foreach (IPropertySymbol indexer in symbols)
            {
                if (hiddenSymbols == null || !hiddenSymbols.Contains(indexer.GetSymbol()))
                {
                    builder.Add(indexer);
                }
            }

            hiddenSymbols?.Free();
            return builder.ToImmutableAndFree();
        }

        /// <remarks>
        /// The method group can contain "duplicate" symbols that we do not want to display in the IDE analysis.
        ///
        /// For example, there could be an overriding virtual method and the method it overrides both in
        /// the method group. This, strictly speaking, is a violation of the C# specification because we are
        /// supposed to strip out overriding methods from the method group before overload resolution; overload
        /// resolution is supposed to treat overridden methods as being methods of the less derived type. However,
        /// in the IDE we want to display information about the overriding method, not the overridden method, and
        /// therefore we leave both in the method group. The overload resolution algorithm has been written
        /// to handle this departure from the specification.
        ///
        /// Similarly, we might have two methods in the method group where one is a "new" method that hides 
        /// another. Again, in overload resolution this would be handled by the rule that says that methods
        /// declared on more derived types take priority over methods declared on less derived types. Both
        /// will be in the method group, but in the IDE we want to only display information about the 
        /// hiding method, not the hidden method.
        ///
        /// We can also have "diamond" inheritance of interfaces leading to multiple copies of the same
        /// method ending up in the method group:
        /// 
        /// interface IB { void M(); }
        /// interface IL : IB {}
        /// interface IR : IB {}
        /// interface ID : IL, IR {}
        /// ...
        /// id.M();
        ///
        /// We only want to display one symbol in the IDE, even if the member lookup algorithm is unsophisticated
        /// and puts IB.M in the member group twice. (Again, this is a mild spec violation since a method group
        /// is supposed to be a set, without duplicates.)
        ///
        /// Finally, the interaction of multiple inheritance of interfaces and hiding can lead to some subtle
        /// situations. Suppose we make a slight modification to the scenario above:
        ///
        /// interface IL : IB { new void M(); } 
        ///
        /// Again, we only want to display one symbol in the method group. The fact that there is a "path"
        /// to IB.M from ID via IR is irrelevant; if the symbol IB.M is hidden by IL.M then it is hidden
        /// in ID, period.
        /// </remarks>
        private static ImmutableArray<MethodSymbol> FilterOverriddenOrHiddenMethods(ImmutableArray<MethodSymbol> methods)
        {
            // Optimization, not required for correctness.
            if (methods.Length <= 1)
            {
                return methods;
            }

            HashSet<Symbol> hiddenSymbols = new HashSet<Symbol>();
            foreach (MethodSymbol method in methods)
            {
                OverriddenOrHiddenMembersResult overriddenOrHiddenMembers = method.OverriddenOrHiddenMembers;

                foreach (Symbol overridden in overriddenOrHiddenMembers.OverriddenMembers)
                {
                    hiddenSymbols.Add(overridden);
                }

                // Don't worry about RuntimeOverriddenMembers - this check is for the API, which
                // should reflect the C# semantics.

                foreach (Symbol hidden in overriddenOrHiddenMembers.HiddenMembers)
                {
                    hiddenSymbols.Add(hidden);
                }
            }

            return methods.WhereAsArray((m, hiddenSymbols) => !hiddenSymbols.Contains(m), hiddenSymbols);
        }

        // Get the symbols and possible method group associated with a method group bound node, as
        // they should be exposed through GetSemanticInfo.
        // NB: It is not safe to pass a null binderOpt during speculative binding.
        // 
        // If the parent node of the method group syntax node provides information (such as arguments) 
        // that allows us to return more specific symbols (a specific overload or applicable candidates)
        // we return these. The complete set of symbols of the method group is then returned in methodGroup parameter.
        private OneOrMany<Symbol> GetMethodGroupSemanticSymbols(
            BoundMethodGroup boundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt,
            out LookupResultKind resultKind,
            out bool isDynamic,
            out ImmutableArray<Symbol> memberGroup)
        {
            Debug.Assert(binderOpt != null || IsInTree(boundNode.Syntax));

            OneOrMany<Symbol> symbols = OneOrMany<Symbol>.Empty;

            resultKind = boundNode.ResultKind;
            if (resultKind == LookupResultKind.Empty)
            {
                resultKind = LookupResultKind.Viable;
            }

            isDynamic = false;

            // The method group needs filtering.
            Binder binder = binderOpt ?? GetEnclosingBinder(GetAdjustedNodePosition(boundNode.Syntax));
            memberGroup = GetReducedAndFilteredMethodGroupSymbols(binder, boundNode);

            // We want to get the actual node chosen by overload resolution, if possible. 
            if (boundNodeForSyntacticParent != null)
            {
                switch (boundNodeForSyntacticParent.Kind)
                {
                    case BoundKind.Call:
                        // If we are looking for info on M in M(args), we want the symbol that overload resolution
                        // chose for M.
                        var call = (BoundCall)boundNodeForSyntacticParent;
                        InvocationExpressionSyntax invocation = call.Syntax as InvocationExpressionSyntax;
                        if (invocation != null && invocation.Expression.SkipParens() == ((ExpressionSyntax)boundNode.Syntax).SkipParens() && (object)call.Method != null)
                        {
                            if (call.OriginalMethodsOpt.IsDefault)
                            {
                                // Overload resolution succeeded.
                                symbols = CreateReducedExtensionMethodIfPossible(call);
                                resultKind = LookupResultKind.Viable;
                            }
                            else
                            {
                                resultKind = call.ResultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                                symbols = CreateReducedAndFilteredSymbolsFromOriginals(call, Compilation);
                            }
                        }
                        break;

                    case BoundKind.DelegateCreationExpression:
                        // If we are looking for info on "M" in "new Action(M)" 
                        // we want to get the symbol that overload resolution chose for M, not the whole method group M.
                        var delegateCreation = (BoundDelegateCreationExpression)boundNodeForSyntacticParent;
                        if (delegateCreation.Argument == boundNode && (object)delegateCreation.MethodOpt != null)
                        {
                            symbols = CreateReducedExtensionMethodIfPossible(delegateCreation, boundNode.ReceiverOpt);
                        }
                        break;

                    case BoundKind.Conversion:
                        // If we are looking for info on "M" in "(Action)M" 
                        // we want to get the symbol that overload resolution chose for M, not the whole method group M.
                        var conversion = (BoundConversion)boundNodeForSyntacticParent;

                        MethodSymbol method = null;
                        if (conversion.ConversionKind == ConversionKind.MethodGroup)
                        {
                            method = conversion.SymbolOpt;
                        }
                        else if (conversion.Operand is BoundConversion { ConversionKind: ConversionKind.MethodGroup } nestedMethodGroupConversion)
                        {
                            method = nestedMethodGroupConversion.SymbolOpt;
                        }

                        if ((object)method != null)
                        {
                            if (conversion.IsExtensionMethod)
                            {
                                method = ReducedExtensionMethodSymbol.Create(method);
                            }

                            symbols = OneOrMany.Create((Symbol)method);
                            resultKind = conversion.ResultKind;
                        }
                        else
                        {
                            goto default;
                        }

                        break;

                    case BoundKind.DynamicInvocation:
                        var dynamicInvocation = (BoundDynamicInvocation)boundNodeForSyntacticParent;
                        symbols = OneOrMany.Create(dynamicInvocation.ApplicableMethods.Cast<MethodSymbol, Symbol>());
                        isDynamic = true;
                        break;

                    case BoundKind.BadExpression:
                        // If the bad expression has symbol(s) from this method group, it better indicates any problems.
                        ImmutableArray<Symbol> myMethodGroup = memberGroup;

                        symbols = OneOrMany.Create(((BoundBadExpression)boundNodeForSyntacticParent).Symbols.WhereAsArray((sym, myMethodGroup) => myMethodGroup.Contains(sym), myMethodGroup));
                        if (symbols.Any())
                        {
                            resultKind = ((BoundBadExpression)boundNodeForSyntacticParent).ResultKind;
                        }
                        break;

                    case BoundKind.NameOfOperator:
                        symbols = OneOrMany.Create(memberGroup);
                        resultKind = resultKind.WorseResultKind(LookupResultKind.MemberGroup);
                        break;

                    default:
                        symbols = OneOrMany.Create(memberGroup);
                        if (symbols.Count > 0)
                        {
                            resultKind = resultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                        }
                        break;
                }
            }
            else if (memberGroup.Length == 1 && !boundNode.HasAnyErrors)
            {
                // During speculative binding, there won't be a parent bound node. The parent bound
                // node may also be absent if the syntactic parent has errors or if one is simply
                // not specified (see SemanticModel.GetSymbolInfoForNode). However, if there's exactly
                // one candidate, then we should probably succeed.

                symbols = OneOrMany.Create(memberGroup);
                if (symbols.Count > 0)
                {
                    resultKind = resultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                }
            }

            if (!symbols.Any())
            {
                // If we didn't find a better set of symbols, then assume this is a method group that didn't
                // get resolved. Return all members of the method group, with a resultKind of OverloadResolutionFailure
                // (unless the method group already has a worse result kind).
                symbols = OneOrMany.Create(memberGroup);
                if (!isDynamic && resultKind > LookupResultKind.OverloadResolutionFailure)
                {
                    resultKind = LookupResultKind.OverloadResolutionFailure;
                }
            }

            return symbols;
        }

        // NB: It is not safe to pass a null binderOpt during speculative binding.
        private OneOrMany<Symbol> GetPropertyGroupSemanticSymbols(
            BoundPropertyGroup boundNode,
            BoundNode boundNodeForSyntacticParent,
            Binder binderOpt,
            out LookupResultKind resultKind,
            out ImmutableArray<Symbol> propertyGroup)
        {
            Debug.Assert(binderOpt != null || IsInTree(boundNode.Syntax));

            OneOrMany<Symbol> symbols = OneOrMany<Symbol>.Empty;

            resultKind = boundNode.ResultKind;
            if (resultKind == LookupResultKind.Empty)
            {
                resultKind = LookupResultKind.Viable;
            }

            // The property group needs filtering.
            propertyGroup = boundNode.Properties.Cast<PropertySymbol, Symbol>();

            // We want to get the actual node chosen by overload resolution, if possible. 
            if (boundNodeForSyntacticParent != null)
            {
                switch (boundNodeForSyntacticParent.Kind)
                {
                    case BoundKind.IndexerAccess:
                        // If we are looking for info on P in P[args], we want the symbol that overload resolution
                        // chose for P.
                        var indexer = (BoundIndexerAccess)boundNodeForSyntacticParent;
                        var elementAccess = indexer.Syntax as ElementAccessExpressionSyntax;
                        if (elementAccess != null && elementAccess.Expression == boundNode.Syntax && (object)indexer.Indexer != null)
                        {
                            if (indexer.OriginalIndexersOpt.IsDefault)
                            {
                                // Overload resolution succeeded.
                                symbols = OneOrMany.Create<Symbol>(indexer.Indexer);
                                resultKind = LookupResultKind.Viable;
                            }
                            else
                            {
                                resultKind = indexer.ResultKind.WorseResultKind(LookupResultKind.OverloadResolutionFailure);
                                symbols = StaticCast<Symbol>.From(OneOrMany.Create(indexer.OriginalIndexersOpt));
                            }
                        }
                        break;

                    case BoundKind.BadExpression:
                        // If the bad expression has symbol(s) from this property group, it better indicates any problems.
                        ImmutableArray<Symbol> myPropertyGroup = propertyGroup;

                        symbols = OneOrMany.Create(((BoundBadExpression)boundNodeForSyntacticParent).Symbols.WhereAsArray((sym, myPropertyGroup) => myPropertyGroup.Contains(sym), myPropertyGroup));
                        if (symbols.Any())
                        {
                            resultKind = ((BoundBadExpression)boundNodeForSyntacticParent).ResultKind;
                        }
                        break;
                }
            }
            else if (propertyGroup.Length == 1 && !boundNode.HasAnyErrors)
            {
                // During speculative binding, there won't be a parent bound node. The parent bound
                // node may also be absent if the syntactic parent has errors or if one is simply
                // not specified (see SemanticModel.GetSymbolInfoForNode). However, if there's exactly
                // one candidate, then we should probably succeed.

                // If we're speculatively binding and there's exactly one candidate, then we should probably succeed.
                symbols = OneOrMany.Create(propertyGroup);
            }

            if (!symbols.Any())
            {
                // If we didn't find a better set of symbols, then assume this is a property group that didn't
                // get resolved. Return all members of the property group, with a resultKind of OverloadResolutionFailure
                // (unless the property group already has a worse result kind).
                symbols = OneOrMany.Create(propertyGroup);
                if (resultKind > LookupResultKind.OverloadResolutionFailure)
                {
                    resultKind = LookupResultKind.OverloadResolutionFailure;
                }
            }

            return symbols;
        }

        /// <summary>
        /// Get the semantic info of a named argument in an invocation-like expression (e.g. `x` in `M(x: 3)`)
        /// or the name in a Subpattern (e.g. either `Name` in `e is (Name: 3){Name: 3}`).
        /// </summary>
        private SymbolInfo GetNamedArgumentSymbolInfo(IdentifierNameSyntax identifierNameSyntax, CancellationToken cancellationToken)
        {
            Debug.Assert(SyntaxFacts.IsNamedArgumentName(identifierNameSyntax));

            // Argument names do not have bound nodes associated with them, so we cannot use the usual
            // GetSymbolInfo mechanism. Instead, we just do the following:
            //   1. Find the containing invocation.
            //   2. Call GetSymbolInfo on that.
            //   3. For each method or indexer in the return semantic info, find the argument
            //      with the given name (if any).
            //   4. Use the ResultKind in that semantic info and any symbols to create the semantic info
            //      for the named argument.
            //   5. Type is always null, as is constant value.

            string argumentName = identifierNameSyntax.Identifier.ValueText;
            if (argumentName.Length == 0)
                return SymbolInfo.None;    // missing name.

            // argument could be an argument of a tuple expression
            // var x = (Identifier: 1, AnotherIdentifier: 2);
            var parent3 = identifierNameSyntax.Parent.Parent.Parent;
            if (parent3.IsKind(SyntaxKind.TupleExpression))
            {
                var tupleArgument = (ArgumentSyntax)identifierNameSyntax.Parent.Parent;
                var tupleElement = GetDeclaredSymbol(tupleArgument, cancellationToken);
                return (object)tupleElement == null ? SymbolInfo.None : new SymbolInfo(tupleElement);
            }

            if (parent3.IsKind(SyntaxKind.PropertyPatternClause) || parent3.IsKind(SyntaxKind.PositionalPatternClause))
            {
                return GetSymbolInfoWorker(identifierNameSyntax, SymbolInfoOptions.DefaultOptions, cancellationToken);
            }

            CSharpSyntaxNode containingInvocation = parent3.Parent;
            SymbolInfo containingInvocationInfo = GetSymbolInfoWorker(containingInvocation, SymbolInfoOptions.PreferConstructorsToType | SymbolInfoOptions.ResolveAliases, cancellationToken);

            if ((object)containingInvocationInfo.Symbol != null)
            {
                ParameterSymbol param = FindNamedParameter(containingInvocationInfo.Symbol.GetSymbol().GetParameters(), argumentName);
                return (object)param == null ? SymbolInfo.None : new SymbolInfo(param.GetPublicSymbol());
            }
            else
            {
                var symbols = ArrayBuilder<ISymbol>.GetInstance();

                foreach (ISymbol invocationSym in containingInvocationInfo.CandidateSymbols)
                {
                    switch (invocationSym.Kind)
                    {
                        case SymbolKind.Method:
                        case SymbolKind.Property:
                            break; // Could have parameters.
                        default:
                            continue; // Definitely doesn't have parameters.
                    }
                    ParameterSymbol param = FindNamedParameter(invocationSym.GetSymbol().GetParameters(), argumentName);
                    if ((object)param != null)
                    {
                        symbols.Add(param.GetPublicSymbol());
                    }
                }

                if (symbols.Count == 0)
                {
                    symbols.Free();
                    return SymbolInfo.None;
                }
                else
                {
                    return new SymbolInfo(symbols.ToImmutableAndFree(), containingInvocationInfo.CandidateReason);
                }
            }
        }

        /// <summary>
        /// Find the first parameter named "argumentName".
        /// </summary>
        private static ParameterSymbol FindNamedParameter(ImmutableArray<ParameterSymbol> parameters, string argumentName)
        {
            foreach (ParameterSymbol param in parameters)
            {
                if (param.Name == argumentName)
                    return param;
            }

            return null;
        }

        internal static ImmutableArray<Symbol> GetReducedAndFilteredMethodGroupSymbols(Binder binder, BoundMethodGroup node)
        {
            var members = ArrayBuilder<Symbol>.GetInstance();
            var filteredMembers = ArrayBuilder<Symbol>.GetInstance();
            var resultKind = LookupResultKind.Empty;
            var typeArguments = node.TypeArgumentsOpt;

            // Non-extension methods.
            if (node.Methods.Any())
            {
                // This is the only place we care about overridden/hidden methods.  If there aren't methods
                // in the method group, there's only one fallback candidate and extension methods never override
                // or hide instance methods or other extension methods.
                ImmutableArray<MethodSymbol> nonHiddenMethods = FilterOverriddenOrHiddenMethods(node.Methods);
                Debug.Assert(nonHiddenMethods.Any()); // Something must be hiding, so can't all be hidden.

                foreach (var method in nonHiddenMethods)
                {
                    MergeReducedAndFilteredSymbol(
                        members,
                        filteredMembers,
                        new SingleLookupResult(node.ResultKind, method, node.LookupError),
                        typeArguments,
                        receiverType: null,
                        ref resultKind,
                        binder.Compilation);
                }
            }
            else
            {
                var otherSymbol = node.LookupSymbolOpt;
                if (((object)otherSymbol != null) && (otherSymbol.Kind == SymbolKind.Method))
                {
                    MergeReducedAndFilteredSymbol(
                        members,
                        filteredMembers,
                        new SingleLookupResult(node.ResultKind, otherSymbol, node.LookupError),
                        typeArguments,
                        receiverType: null,
                        ref resultKind,
                        binder.Compilation);
                }
            }

            var receiver = node.ReceiverOpt;
            var name = node.Name;

            // Extension members, all scopes.
            if (node.SearchExtensions && receiver.Type is { } receiverType)
            {
                Debug.Assert(receiver != null);
                int arity;
                LookupOptions options;
                if (typeArguments.IsDefault)
                {
                    arity = 0;
                    options = LookupOptions.AllMethodsOnArityZero;
                }
                else
                {
                    arity = typeArguments.Length;
                    options = LookupOptions.Default;
                }

                binder = binder.WithAdditionalFlags(BinderFlags.SemanticModel);
                var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;

                var singleLookupResults = ArrayBuilder<SingleLookupResult>.GetInstance();
                foreach (var scope in new ExtensionScopes(binder))
                {
                    singleLookupResults.Clear();
                    scope.Binder.EnumerateAllExtensionMembersInSingleBinder(singleLookupResults, name, arity, options, originalBinder: binder, useSiteInfo: ref discardedUseSiteInfo, classicExtensionUseSiteInfo: ref discardedUseSiteInfo);

                    foreach (SingleLookupResult singleLookupResult in singleLookupResults)
                    {
                        if (singleLookupResult.Symbol is not (MethodSymbol or PropertySymbol))
                        {
                            continue;
                        }

                        MergeReducedAndFilteredSymbol(
                            members,
                            filteredMembers,
                            singleLookupResult,
                            typeArguments,
                            receiverType,
                            ref resultKind,
                            binder.Compilation);
                    }
                }

                singleLookupResults.Free();
            }

            members.Free();
            return filteredMembers.ToImmutableAndFree();
        }

#nullable enable
        // Reduce classic extension methods to their reduced form, and remove:
        //   a) Extension methods are aren't applicable to receiverType
        //   including constraint checking.
        //   b) Duplicate methods
        //   c) Methods that are hidden or overridden by another method in the group.
        // For new extension members, infer type arguments for the extension declaration based on the receiver type,
        //   perform the substitution, and remove:
        //   a) Members that would break constraints
        //   b) Members that are not applicable to the receiver type.
        private static bool AddReducedAndFilteredSymbol(
            ArrayBuilder<Symbol> members,
            ArrayBuilder<Symbol> filteredMembers,
            Symbol member,
            ImmutableArray<TypeWithAnnotations> typeArguments,
            TypeSymbol receiverType,
            CSharpCompilation compilation)
        {
            Symbol? substitutedMember = member.GetReducedAndFilteredSymbol(typeArguments, receiverType, compilation, checkFullyInferred: false);
            if (substitutedMember is null)
            {
                return false;
            }

            // Don't add exact duplicates.
            if (filteredMembers.Contains(substitutedMember))
            {
                return false;
            }

            members.Add(member);
            filteredMembers.Add(substitutedMember);
            return true;
        }
#nullable disable

        private static void MergeReducedAndFilteredSymbol(
            ArrayBuilder<Symbol> members,
            ArrayBuilder<Symbol> filteredMembers,
            SingleLookupResult singleResult,
            ImmutableArray<TypeWithAnnotations> typeArguments,
            TypeSymbol receiverType,
            ref LookupResultKind resultKind,
            CSharpCompilation compilation)
        {
            if (singleResult.Symbol is null)
            {
                return;
            }

            Symbol member = singleResult.Symbol;

            LookupResultKind singleKind = singleResult.Kind;
            if (resultKind > singleKind)
            {
                return;
            }
            else if (resultKind < singleKind)
            {
                members.Clear();
                filteredMembers.Clear();
                resultKind = LookupResultKind.Empty;
            }

            if (AddReducedAndFilteredSymbol(members, filteredMembers, member, typeArguments, receiverType, compilation))
            {
                Debug.Assert(members.Count > 0);
                if (resultKind < singleKind)
                {
                    resultKind = singleKind;
                }
            }

            Debug.Assert((members.Count == 0) == (resultKind == LookupResultKind.Empty));
            Debug.Assert(members.Count == filteredMembers.Count);
        }

        /// <summary>
        /// If the call represents a classic extension method invocation with an explicit receiver, return the original
        /// methods as ReducedExtensionMethodSymbols. Otherwise, return the original methods unchanged.
        /// </summary>
        private static OneOrMany<Symbol> CreateReducedAndFilteredSymbolsFromOriginals(BoundCall call, CSharpCompilation compilation)
        {
            var methods = call.OriginalMethodsOpt;
            TypeSymbol receiverType = null;
            Debug.Assert(!methods.IsDefault);

            // Note: A call including new extension members may be marked as InvokedAsExtensionMethod in error scenarios
            if (call.InvokedAsExtensionMethod)
            {
                if (call.ReceiverOpt != null)
                {
                    receiverType = call.ReceiverOpt.Type;
                }
                else
                {
                    receiverType = call.Arguments[0].Type;
                }

                Debug.Assert((object)receiverType != null);
            }

            var methodBuilder = ArrayBuilder<Symbol>.GetInstance();
            var filteredMethodBuilder = ArrayBuilder<Symbol>.GetInstance();
            foreach (var method in FilterOverriddenOrHiddenMethods(methods))
            {
                AddReducedAndFilteredSymbol(methodBuilder, filteredMethodBuilder, method, typeArguments: default, receiverType, compilation);
            }
            methodBuilder.Free();
            return filteredMethodBuilder.ToOneOrManyAndFree();
        }

        /// <summary>
        /// If the call represents an extension method with an explicit receiver, return a
        /// ReducedExtensionMethodSymbol if it can be constructed. Otherwise, return the 
        /// original call method.
        /// </summary>
        private OneOrMany<Symbol> CreateReducedExtensionMethodIfPossible(BoundCall call)
        {
            var method = call.Method;
            Debug.Assert((object)method != null);

            if (call.InvokedAsExtensionMethod && method.IsExtensionMethod && method.MethodKind != MethodKind.ReducedExtension)
            {
                Debug.Assert(call.Arguments.Length > 0);
                BoundExpression receiver = call.Arguments[0];
                MethodSymbol reduced = method.ReduceExtensionMethod(receiver.Type, Compilation);
                // If the extension method can't be applied to the receiver of the given
                // type, we should also return the original call method.
                method = reduced ?? method;
            }
            return OneOrMany.Create<Symbol>(method);
        }

        private OneOrMany<Symbol> CreateReducedExtensionMethodIfPossible(BoundDelegateCreationExpression delegateCreation, BoundExpression receiverOpt)
        {
            var method = delegateCreation.MethodOpt;
            Debug.Assert((object)method != null);

            if (delegateCreation.IsExtensionMethod && method.IsExtensionMethod && (receiverOpt != null))
            {
                MethodSymbol reduced = method.ReduceExtensionMethod(receiverOpt.Type, Compilation);
                method = reduced ?? method;
            }
            return OneOrMany.Create<Symbol>(method);
        }

        /// <summary>
        /// Gets for each statement info.
        /// </summary>
        /// <param name="node">The node.</param>
        public abstract ForEachStatementInfo GetForEachStatementInfo(ForEachStatementSyntax node);

        /// <summary>
        /// Gets for each statement info.
        /// </summary>
        /// <param name="node">The node.</param>
        public abstract ForEachStatementInfo GetForEachStatementInfo(CommonForEachStatementSyntax node);

        /// <summary>
        /// Gets deconstruction assignment info.
        /// </summary>
        /// <param name="node">The node.</param>
        public abstract DeconstructionInfo GetDeconstructionInfo(AssignmentExpressionSyntax node);

        /// <summary>
        /// Gets deconstruction foreach info.
        /// </summary>
        /// <param name="node">The node.</param>
        public abstract DeconstructionInfo GetDeconstructionInfo(ForEachVariableStatementSyntax node);

        /// <summary>
        /// Gets await expression info.
        /// </summary>
        /// <param name="node">The node.</param>
        public abstract AwaitExpressionInfo GetAwaitExpressionInfo(AwaitExpressionSyntax node);

        /// <summary>
        /// If the given node is within a preprocessing directive, gets the preprocessing symbol info for it.
        /// </summary>
        /// <param name="node">Preprocessing symbol identifier node.</param>
        public PreprocessingSymbolInfo GetPreprocessingSymbolInfo(IdentifierNameSyntax node)
        {
            CheckSyntaxNode(node);

            if (node.Ancestors().Any(n => SyntaxFacts.IsPreprocessorDirective(n.Kind())))
            {
                bool isDefined = this.SyntaxTree.IsPreprocessorSymbolDefined(node.Identifier.ValueText, node.Identifier.SpanStart);
                return new PreprocessingSymbolInfo(new Symbols.PublicModel.PreprocessingSymbol(node.Identifier.ValueText), isDefined);
            }

            return PreprocessingSymbolInfo.None;
        }

        /// <summary>
        /// Options to control the internal working of GetSymbolInfoWorker. Not currently exposed
        /// to public clients, but could be if desired.
        /// </summary>
        [Flags]
        internal enum SymbolInfoOptions
        {
            /// <summary>
            /// When binding "C" new C(...), return the type C and do not return information about
            /// which constructor was bound to. Bind "new C(...)" to get information about which constructor
            /// was chosen.
            /// </summary>
            PreferTypeToConstructors = 0x1,

            /// <summary>
            /// When binding "C" new C(...), return the constructor of C that was bound to, if C unambiguously
            /// binds to a single type with at least one constructor. 
            /// </summary>
            PreferConstructorsToType = 0x2,

            /// <summary>
            /// When binding a name X that was declared with a "using X=OtherTypeOrNamespace", return OtherTypeOrNamespace.
            /// </summary>
            ResolveAliases = 0x4,

            /// <summary>
            /// When binding a name X that was declared with a "using X=OtherTypeOrNamespace", return the alias symbol X.
            /// </summary>
            PreserveAliases = 0x8,

            // Default Options.
            DefaultOptions = PreferConstructorsToType | ResolveAliases
        }

        internal static void ValidateSymbolInfoOptions(SymbolInfoOptions options)
        {
            Debug.Assert(((options & SymbolInfoOptions.PreferConstructorsToType) != 0) !=
                         ((options & SymbolInfoOptions.PreferTypeToConstructors) != 0), "Options are mutually exclusive");
            Debug.Assert(((options & SymbolInfoOptions.ResolveAliases) != 0) !=
                         ((options & SymbolInfoOptions.PreserveAliases) != 0), "Options are mutually exclusive");
        }

        /// <summary>
        /// Given a position in the SyntaxTree for this SemanticModel returns the innermost
        /// NamedType that the position is considered inside of.
        /// </summary>
        public ISymbol GetEnclosingSymbol(int position)
        {
            position = CheckAndAdjustPosition(position);
            var binder = GetEnclosingBinder(position);
            return binder == null ? null : binder.ContainingMemberOrLambda.GetPublicSymbol();
        }

        #region SemanticModel Members

        public sealed override string Language
        {
            get
            {
                return LanguageNames.CSharp;
            }
        }

        protected sealed override Compilation CompilationCore
        {
            get
            {
                return this.Compilation;
            }
        }

        protected sealed override SemanticModel ParentModelCore
        {
            get
            {
                return this.ParentModel;
            }
        }

        protected sealed override SyntaxTree SyntaxTreeCore
        {
            get
            {
                return this.SyntaxTree;
            }
        }

        protected sealed override SyntaxNode RootCore => this.Root;

        private SymbolInfo GetSymbolInfoFromNode(SyntaxNode node, CancellationToken cancellationToken)
        {
            switch (node)
            {
                case null:
                    throw new ArgumentNullException(nameof(node));
                case ExpressionSyntax expression:
                    return this.GetSymbolInfo(expression, cancellationToken);
                case ConstructorInitializerSyntax initializer:
                    return this.GetSymbolInfo(initializer, cancellationToken);
                case PrimaryConstructorBaseTypeSyntax initializer:
                    return this.GetSymbolInfo(initializer, cancellationToken);
                case AttributeSyntax attribute:
                    return this.GetSymbolInfo(attribute, cancellationToken);
                case CrefSyntax cref:
                    return this.GetSymbolInfo(cref, cancellationToken);
                case SelectOrGroupClauseSyntax selectOrGroupClause:
                    return this.GetSymbolInfo(selectOrGroupClause, cancellationToken);
                case OrderingSyntax orderingSyntax:
                    return this.GetSymbolInfo(orderingSyntax, cancellationToken);
                case PositionalPatternClauseSyntax ppcSyntax:
                    return this.GetSymbolInfo(ppcSyntax, cancellationToken);
            }

            return SymbolInfo.None;
        }

        private TypeInfo GetTypeInfoFromNode(SyntaxNode node, CancellationToken cancellationToken)
        {
            switch (node)
            {
                case null:
                    throw new ArgumentNullException(nameof(node));
                case ExpressionSyntax expression:
                    return this.GetTypeInfo(expression, cancellationToken);
                case ConstructorInitializerSyntax initializer:
                    return this.GetTypeInfo(initializer, cancellationToken);
                case AttributeSyntax attribute:
                    return this.GetTypeInfo(attribute, cancellationToken);
                case SelectOrGroupClauseSyntax selectOrGroupClause:
                    return this.GetTypeInfo(selectOrGroupClause, cancellationToken);
                case PatternSyntax pattern:
                    return this.GetTypeInfo(pattern, cancellationToken);
            }

            return CSharpTypeInfo.None;
        }

        private ImmutableArray<ISymbol> GetMemberGroupFromNode(SyntaxNode node, CancellationToken cancellationToken)
        {
            switch (node)
            {
                case null:
                    throw new ArgumentNullException(nameof(node));
                case ExpressionSyntax expression:
                    return this.GetMemberGroup(expression, cancellationToken);
                case ConstructorInitializerSyntax initializer:
                    return this.GetMemberGroup(initializer, cancellationToken);
                case AttributeSyntax attribute:
                    return this.GetMemberGroup(attribute, cancellationToken);
            }

            return ImmutableArray<ISymbol>.Empty;
        }

        protected sealed override ImmutableArray<ISymbol> GetMemberGroupCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            var methodGroup = this.GetMemberGroupFromNode(node, cancellationToken);
            return StaticCast<ISymbol>.From(methodGroup);
        }

        protected sealed override SymbolInfo GetSpeculativeSymbolInfoCore(int position, SyntaxNode node, SpeculativeBindingOption bindingOption)
        {
            switch (node)
            {
                case ExpressionSyntax expression:
                    return GetSpeculativeSymbolInfo(position, expression, bindingOption);
                case ConstructorInitializerSyntax initializer:
                    return GetSpeculativeSymbolInfo(position, initializer);
                case PrimaryConstructorBaseTypeSyntax initializer:
                    return GetSpeculativeSymbolInfo(position, initializer);
                case AttributeSyntax attribute:
                    return GetSpeculativeSymbolInfo(position, attribute);
                case CrefSyntax cref:
                    return GetSpeculativeSymbolInfo(position, cref);
            }

            return SymbolInfo.None;
        }

        protected sealed override TypeInfo GetSpeculativeTypeInfoCore(int position, SyntaxNode node, SpeculativeBindingOption bindingOption)
        {
            return node is ExpressionSyntax expression
                ? GetSpeculativeTypeInfo(position, expression, bindingOption)
                : CSharpTypeInfo.None;
        }

        protected sealed override IAliasSymbol GetSpeculativeAliasInfoCore(int position, SyntaxNode nameSyntax, SpeculativeBindingOption bindingOption)
        {
            return nameSyntax is IdentifierNameSyntax identifier
                ? GetSpeculativeAliasInfo(position, identifier, bindingOption)
                : null;
        }

        protected sealed override SymbolInfo GetSymbolInfoCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            return this.GetSymbolInfoFromNode(node, cancellationToken);
        }

        protected sealed override TypeInfo GetTypeInfoCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            return this.GetTypeInfoFromNode(node, cancellationToken);
        }

        protected sealed override IAliasSymbol GetAliasInfoCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            return node is IdentifierNameSyntax nameSyntax ? GetAliasInfo(nameSyntax, cancellationToken) : null;
        }

        protected sealed override PreprocessingSymbolInfo GetPreprocessingSymbolInfoCore(SyntaxNode node)
        {
            return node is IdentifierNameSyntax nameSyntax
                ? GetPreprocessingSymbolInfo(nameSyntax)
                : PreprocessingSymbolInfo.None;
        }

        protected sealed override ISymbol GetDeclaredSymbolCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (node)
            {
                case AccessorDeclarationSyntax accessor:
                    return this.GetDeclaredSymbol(accessor, cancellationToken);
                case BaseTypeDeclarationSyntax type:
                    return this.GetDeclaredSymbol(type, cancellationToken);
                case QueryClauseSyntax clause:
                    return this.GetDeclaredSymbol(clause, cancellationToken);
                case MemberDeclarationSyntax member:
                    return this.GetDeclaredSymbol(member, cancellationToken);
            }

            switch (node.Kind())
            {
                case SyntaxKind.LocalFunctionStatement:
                    return this.GetDeclaredSymbol((LocalFunctionStatementSyntax)node, cancellationToken);
                case SyntaxKind.LabeledStatement:
                    return this.GetDeclaredSymbol((LabeledStatementSyntax)node, cancellationToken);
                case SyntaxKind.CaseSwitchLabel:
                case SyntaxKind.DefaultSwitchLabel:
                    return this.GetDeclaredSymbol((SwitchLabelSyntax)node, cancellationToken);
                case SyntaxKind.AnonymousObjectCreationExpression:
                    return this.GetDeclaredSymbol((AnonymousObjectCreationExpressionSyntax)node, cancellationToken);
                case SyntaxKind.AnonymousObjectMemberDeclarator:
                    return this.GetDeclaredSymbol((AnonymousObjectMemberDeclaratorSyntax)node, cancellationToken);
                case SyntaxKind.TupleExpression:
                    return this.GetDeclaredSymbol((TupleExpressionSyntax)node, cancellationToken);
                case SyntaxKind.Argument:
                    return this.GetDeclaredSymbol((ArgumentSyntax)node, cancellationToken);
                case SyntaxKind.VariableDeclarator:
                    return this.GetDeclaredSymbol((VariableDeclaratorSyntax)node, cancellationToken);
                case SyntaxKind.SingleVariableDesignation:
                    return this.GetDeclaredSymbol((SingleVariableDesignationSyntax)node, cancellationToken);
                case SyntaxKind.TupleElement:
                    return this.GetDeclaredSymbol((TupleElementSyntax)node, cancellationToken);
                case SyntaxKind.NamespaceDeclaration:
                    return this.GetDeclaredSymbol((NamespaceDeclarationSyntax)node, cancellationToken);
                case SyntaxKind.FileScopedNamespaceDeclaration:
                    return this.GetDeclaredSymbol((FileScopedNamespaceDeclarationSyntax)node, cancellationToken);
                case SyntaxKind.Parameter:
                    return this.GetDeclaredSymbol((ParameterSyntax)node, cancellationToken);
                case SyntaxKind.TypeParameter:
                    return this.GetDeclaredSymbol((TypeParameterSyntax)node, cancellationToken);
                case SyntaxKind.UsingDirective:
                    var usingDirective = (UsingDirectiveSyntax)node;
                    if (usingDirective.Alias == null)
                    {
                        break;
                    }

                    return this.GetDeclaredSymbol(usingDirective, cancellationToken);
                case SyntaxKind.ForEachStatement:
                    return this.GetDeclaredSymbol((ForEachStatementSyntax)node);
                case SyntaxKind.CatchDeclaration:
                    return this.GetDeclaredSymbol((CatchDeclarationSyntax)node);
                case SyntaxKind.JoinIntoClause:
                    return this.GetDeclaredSymbol((JoinIntoClauseSyntax)node, cancellationToken);
                case SyntaxKind.QueryContinuation:
                    return this.GetDeclaredSymbol((QueryContinuationSyntax)node, cancellationToken);
                case SyntaxKind.CompilationUnit:
                    return this.GetDeclaredSymbol((CompilationUnitSyntax)node, cancellationToken);
            }

            return null;
        }

        /// <summary>
        /// Given a tuple element syntax, get the corresponding symbol.
        /// </summary>
        /// <param name="declarationSyntax">The syntax node that declares a tuple element.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The symbol that was declared.</returns>
        public ISymbol GetDeclaredSymbol(TupleElementSyntax declarationSyntax, CancellationToken cancellationToken = default(CancellationToken))
        {
            CheckSyntaxNode(declarationSyntax);

            if (declarationSyntax.Parent is TupleTypeSyntax tupleTypeSyntax)
            {
                return (GetSymbolInfo(tupleTypeSyntax, cancellationToken).Symbol.GetSymbol() as NamedTypeSymbol)?.TupleElements.ElementAtOrDefault(tupleTypeSyntax.Elements.IndexOf(declarationSyntax)).GetPublicSymbol();
            }

            return null;
        }

        protected sealed override ImmutableArray<ISymbol> GetDeclaredSymbolsCore(SyntaxNode declaration, CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (declaration is BaseFieldDeclarationSyntax field)
            {
                return this.GetDeclaredSymbols(field, cancellationToken);
            }

            // If the type decl has a primary constructor, return that symbol as well.  This is needed so that if the
            // 'suppression' or 'generated code' attribute is on the primary constructor (i.e. by using `[method:
            // SuppressMessage(...)]`, it will be found when walking up to the type declaration.
            if (declaration is TypeDeclarationSyntax typeDeclaration)
            {
                var namedType = GetDeclaredSymbol(typeDeclaration, cancellationToken);
                var primaryConstructor = TryGetSynthesizedPrimaryConstructor(
                    typeDeclaration, namedType.GetSymbol<NamedTypeSymbol>());

                return primaryConstructor is null
                    ? ImmutableArray.Create<ISymbol>(namedType)
                    : ImmutableArray.Create<ISymbol>(namedType, primaryConstructor.GetPublicSymbol());
            }

            var symbol = GetDeclaredSymbolCore(declaration, cancellationToken);
            return symbol != null
                ? ImmutableArray.Create(symbol)
                : ImmutableArray<ISymbol>.Empty;
        }

#nullable enable
        public IMethodSymbol? GetInterceptorMethod(InvocationExpressionSyntax node, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CheckSyntaxNode(node);

            if (node.GetInterceptableNameSyntax() is { } nameSyntax && Compilation.TryGetInterceptor(nameSyntax) is (_, MethodSymbol interceptor))
            {
                return interceptor.GetPublicSymbol();
            }

            return null;
        }

#pragma warning disable RSEXPERIMENTAL002 // Internal usage of experimental API
        public InterceptableLocation? GetInterceptableLocation(InvocationExpressionSyntax node, CancellationToken cancellationToken)
        {
            CheckSyntaxNode(node);
            if (node.GetInterceptableNameSyntax() is not { } nameSyntax)
            {
                return null;
            }

            return GetInterceptableLocationInternal(nameSyntax, cancellationToken);
        }

        // Factored out for ease of test authoring, especially for scenarios involving unsupported syntax.
        internal InterceptableLocation GetInterceptableLocationInternal(SyntaxNode nameSyntax, CancellationToken cancellationToken)
        {
            var tree = nameSyntax.SyntaxTree;
            var text = tree.GetText(cancellationToken);
            var path = tree.FilePath;
            var checksum = text.GetContentHash();

            var lineSpan = nameSyntax.Location.GetLineSpan().Span.Start;
            var lineNumberOneIndexed = lineSpan.Line + 1;
            var characterNumberOneIndexed = lineSpan.Character + 1;

            return new InterceptableLocation1(checksum, path, Compilation.Options.SourceReferenceResolver, nameSyntax.Position, lineNumberOneIndexed, characterNumberOneIndexed);
        }
#nullable disable

        protected static SynthesizedPrimaryConstructor TryGetSynthesizedPrimaryConstructor(TypeDeclarationSyntax node, NamedTypeSymbol type)
        {
            if (type is SourceMemberContainerTypeSymbol { PrimaryConstructor: { } primaryConstructor }
                && primaryConstructor.SyntaxRef.SyntaxTree == node.SyntaxTree
                && primaryConstructor.GetSyntax() == node)
            {
                return primaryConstructor;
            }

            return null;
        }

        internal override void ComputeDeclarationsInSpan(TextSpan span, bool getSymbol, ArrayBuilder<DeclarationInfo> builder, CancellationToken cancellationToken)
        {
            CSharpDeclarationComputer.ComputeDeclarationsInSpan(this, span, getSymbol, builder, cancellationToken);
        }

        internal override void ComputeDeclarationsInNode(SyntaxNode node, ISymbol associatedSymbol, bool getSymbol, ArrayBuilder<DeclarationInfo> builder, CancellationToken cancellationToken, int? levelsToCompute = null)
        {
            CSharpDeclarationComputer.ComputeDeclarationsInNode(this, associatedSymbol, node, getSymbol, builder, cancellationToken, levelsToCompute);
        }

        internal abstract override Func<SyntaxNode, bool> GetSyntaxNodesToAnalyzeFilter(SyntaxNode declaredNode, ISymbol declaredSymbol);
        internal abstract override bool ShouldSkipSyntaxNodeAnalysis(SyntaxNode node, ISymbol containingSymbol);

        protected internal override SyntaxNode GetTopmostNodeForDiagnosticAnalysis(ISymbol symbol, SyntaxNode declaringSyntax)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.Event:  // for field-like events
                case SymbolKind.Field:
                    var fieldDecl = declaringSyntax.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>();
                    if (fieldDecl != null)
                    {
                        return fieldDecl;
                    }

                    break;
            }

            return declaringSyntax;
        }

        protected sealed override ImmutableArray<ISymbol> LookupSymbolsCore(int position, INamespaceOrTypeSymbol container, string name, bool includeReducedExtensionMethods)
        {
            return LookupSymbols(position, container.EnsureCSharpSymbolOrNull(nameof(container)), name, includeReducedExtensionMethods);
        }

        protected sealed override ImmutableArray<ISymbol> LookupBaseMembersCore(int position, string name)
        {
            return LookupBaseMembers(position, name);
        }

        protected sealed override ImmutableArray<ISymbol> LookupStaticMembersCore(int position, INamespaceOrTypeSymbol container, string name)
        {
            return LookupStaticMembers(position, container.EnsureCSharpSymbolOrNull(nameof(container)), name);
        }

        protected sealed override ImmutableArray<ISymbol> LookupNamespacesAndTypesCore(int position, INamespaceOrTypeSymbol container, string name)
        {
            return LookupNamespacesAndTypes(position, container.EnsureCSharpSymbolOrNull(nameof(container)), name);
        }

        protected sealed override ImmutableArray<ISymbol> LookupLabelsCore(int position, string name)
        {
            return LookupLabels(position, name);
        }

        protected sealed override ControlFlowAnalysis AnalyzeControlFlowCore(SyntaxNode firstStatement, SyntaxNode lastStatement)
        {
            if (firstStatement == null)
            {
                throw new ArgumentNullException(nameof(firstStatement));
            }

            if (lastStatement == null)
            {
                throw new ArgumentNullException(nameof(lastStatement));
            }

            if (!(firstStatement is StatementSyntax firstStatementSyntax))
            {
                throw new ArgumentException("firstStatement is not a StatementSyntax.");
            }

            if (!(lastStatement is StatementSyntax lastStatementSyntax))
            {
                throw new ArgumentException("firstStatement is a StatementSyntax but lastStatement isn't.");
            }

            return this.AnalyzeControlFlow(firstStatementSyntax, lastStatementSyntax);
        }

        protected sealed override ControlFlowAnalysis AnalyzeControlFlowCore(SyntaxNode statement)
        {
            if (statement == null)
            {
                throw new ArgumentNullException(nameof(statement));
            }

            if (!(statement is StatementSyntax statementSyntax))
            {
                throw new ArgumentException("statement is not a StatementSyntax.");
            }

            return this.AnalyzeControlFlow(statementSyntax);
        }

        protected sealed override DataFlowAnalysis AnalyzeDataFlowCore(SyntaxNode firstStatement, SyntaxNode lastStatement)
        {
            if (firstStatement == null)
            {
                throw new ArgumentNullException(nameof(firstStatement));
            }

            if (lastStatement == null)
            {
                throw new ArgumentNullException(nameof(lastStatement));
            }

            if (!(firstStatement is StatementSyntax firstStatementSyntax))
            {
                throw new ArgumentException("firstStatement is not a StatementSyntax.");
            }

            if (!(lastStatement is StatementSyntax lastStatementSyntax))
            {
                throw new ArgumentException("lastStatement is not a StatementSyntax.");
            }

            return this.AnalyzeDataFlow(firstStatementSyntax, lastStatementSyntax);
        }

        protected sealed override DataFlowAnalysis AnalyzeDataFlowCore(SyntaxNode statementOrExpression)
        {
            switch (statementOrExpression)
            {
                case null:
                    throw new ArgumentNullException(nameof(statementOrExpression));
                case StatementSyntax statementSyntax:
                    return this.AnalyzeDataFlow(statementSyntax);
                case ExpressionSyntax expressionSyntax:
                    return this.AnalyzeDataFlow(expressionSyntax);
                case ConstructorInitializerSyntax constructorInitializer:
                    return this.AnalyzeDataFlow(constructorInitializer);
                case PrimaryConstructorBaseTypeSyntax primaryConstructorBaseType:
                    return this.AnalyzeDataFlow(primaryConstructorBaseType);
                default:
                    throw new ArgumentException("statementOrExpression is not a StatementSyntax or an ExpressionSyntax or a ConstructorInitializerSyntax or a PrimaryConstructorBaseTypeSyntax.");
            }
        }

        protected sealed override Optional<object> GetConstantValueCore(SyntaxNode node, CancellationToken cancellationToken)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            return node is ExpressionSyntax expression
                ? GetConstantValue(expression, cancellationToken)
                : default(Optional<object>);
        }

        protected sealed override ISymbol GetEnclosingSymbolCore(int position, CancellationToken cancellationToken)
        {
            return this.GetEnclosingSymbol(position);
        }

        private protected sealed override ImmutableArray<IImportScope> GetImportScopesCore(int position, CancellationToken cancellationToken)
        {
            position = CheckAndAdjustPosition(position);
            var binder = GetEnclosingBinder(position);
            var builder = ArrayBuilder<IImportScope>.GetInstance();

            for (var chain = binder?.ImportChain; chain != null; chain = chain.ParentOpt)
            {
                var imports = chain.Imports;
                if (imports.IsEmpty)
                    continue;

                // Try to create a node corresponding to the imports of the next higher binder scope. Then create the
                // node corresponding to this set of imports and chain it to that.
                builder.Add(new SimpleImportScope(
                    imports.UsingAliases.SelectAsArray(static kvp => kvp.Value.Alias.GetPublicSymbol()),
                    imports.ExternAliases.SelectAsArray(static e => e.Alias.GetPublicSymbol()),
                    imports.Usings.SelectAsArray(static n => new ImportedNamespaceOrType(n.NamespaceOrType.GetPublicSymbol(), n.UsingDirectiveReference)),
                    xmlNamespaces: ImmutableArray<ImportedXmlNamespace>.Empty));
            }

            return builder.ToImmutableAndFree();
        }

        protected sealed override bool IsAccessibleCore(int position, ISymbol symbol)
        {
            return this.IsAccessible(position, symbol.EnsureCSharpSymbolOrNull(nameof(symbol)));
        }

        protected sealed override bool IsEventUsableAsFieldCore(int position, IEventSymbol symbol)
        {
            return this.IsEventUsableAsField(position, symbol.EnsureCSharpSymbolOrNull(nameof(symbol)));
        }

        public sealed override NullableContext GetNullableContext(int position)
        {
            var syntaxTree = (CSharpSyntaxTree)Root.SyntaxTree;

            NullableContextOptions? lazyDefaultState = null;
            NullableContextState contextState = syntaxTree.GetNullableContextState(position);

            return contextState.AnnotationsState switch
            {
                NullableContextState.State.Enabled => NullableContext.AnnotationsEnabled,
                NullableContextState.State.Disabled => NullableContext.Disabled,
                _ when getDefaultState().AnnotationsEnabled() => NullableContext.AnnotationsContextInherited | NullableContext.AnnotationsEnabled,
                _ => NullableContext.AnnotationsContextInherited,
            }
            | contextState.WarningsState switch
            {
                NullableContextState.State.Enabled => NullableContext.WarningsEnabled,
                NullableContextState.State.Disabled => NullableContext.Disabled,
                _ when getDefaultState().WarningsEnabled() => NullableContext.WarningsContextInherited | NullableContext.WarningsEnabled,
                _ => NullableContext.WarningsContextInherited,
            };

            // IsGeneratedCode might be slow, only call it when needed:
            NullableContextOptions getDefaultState()
                => lazyDefaultState ??= syntaxTree.IsGeneratedCode(Compilation.Options.SyntaxTreeOptionsProvider, CancellationToken.None)
                    ? NullableContextOptions.Disable
                    : Compilation.Options.NullableContextOptions;
        }

        #endregion
    }
}
