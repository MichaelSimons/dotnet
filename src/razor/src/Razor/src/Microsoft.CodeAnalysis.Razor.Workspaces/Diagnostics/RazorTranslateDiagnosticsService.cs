﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.AspNetCore.Razor.PooledObjects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor.DocumentMapping;
using Microsoft.CodeAnalysis.Razor.Logging;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Razor.Diagnostics;

using RazorDiagnosticFactory = AspNetCore.Razor.Language.RazorDiagnosticFactory;
using SyntaxNode = AspNetCore.Razor.Language.Syntax.SyntaxNode;
using RazorSyntaxNodeOrToken = AspNetCore.Razor.Language.Syntax.SyntaxNodeOrToken;

/// <summary>
/// Contains several methods for mapping and filtering Razor and C# diagnostics. It allows for
/// translating code diagnostics from one representation into another, such as from C# to Razor.
/// </summary>
/// <param name="documentMappingService">The <see cref="IDocumentMappingService"/>.</param>
/// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
/// <exception cref="ArgumentNullException"/>
internal class RazorTranslateDiagnosticsService(IDocumentMappingService documentMappingService, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.GetOrCreateLogger<RazorTranslateDiagnosticsService>();
    private readonly IDocumentMappingService _documentMappingService = documentMappingService;

    private static readonly FrozenSet<string> s_cSharpDiagnosticsToIgnore = new HashSet<string>(
    [
        "RemoveUnnecessaryImportsFixable",
        "IDE0005_gen", // Using directive is unnecessary
    ]).ToFrozenSet();

    /// <summary>
    ///  Translates code diagnostics from one representation into another.
    /// </summary>
    /// <param name="diagnosticKind">
    ///  The <see cref="RazorLanguageKind"/> of the <see cref="Diagnostic"/> objects
    ///  included in <paramref name="diagnostics"/>.
    /// </param>
    /// <param name="diagnostics">
    ///  An array of <see cref="Diagnostic"/> objects to translate.
    /// </param>
    /// <param name="documentSnapshot">
    ///  The <see cref="IDocumentSnapshot"/> for the code document associated with the diagnostics.
    /// </param>
    /// <param name="cancellationToken">A token that can be checked to cancel work.</param>
    /// <returns>An array of translated diagnostics</returns>
    internal async Task<LspDiagnostic[]> TranslateAsync(
        RazorLanguageKind diagnosticKind,
        LspDiagnostic[] diagnostics,
        IDocumentSnapshot documentSnapshot,
        CancellationToken cancellationToken)
    {
        var codeDocument = await documentSnapshot.GetGeneratedOutputAsync(cancellationToken).ConfigureAwait(false);

        var filteredDiagnostics = diagnosticKind == RazorLanguageKind.CSharp
            ? FilterCSharpDiagnostics(diagnostics, codeDocument)
            : FilterHTMLDiagnostics(diagnostics, codeDocument);
        if (filteredDiagnostics.Length == 0)
        {
            _logger.LogDebug($"No diagnostics remaining after filtering.");
            return [];
        }

        _logger.LogDebug($"{filteredDiagnostics.Length}/{diagnostics.Length} diagnostics remain after filtering {diagnosticKind}.");

        var mappedDiagnostics = MapDiagnostics(
            diagnosticKind,
            filteredDiagnostics,
            documentSnapshot,
            codeDocument);

        return mappedDiagnostics;
    }

    private LspDiagnostic[] FilterCSharpDiagnostics(LspDiagnostic[] unmappedDiagnostics, RazorCodeDocument codeDocument)
    {
        return unmappedDiagnostics.Where(d =>
            !ShouldFilterCSharpDiagnosticBasedOnErrorCode(d, codeDocument)).ToArray();
    }

    private static LspDiagnostic[] FilterHTMLDiagnostics(
        LspDiagnostic[] unmappedDiagnostics,
        RazorCodeDocument codeDocument)
    {
        var syntaxTree = codeDocument.GetRequiredSyntaxTree();
        var sourceText = codeDocument.Source.Text;

        var processedAttributes = new Dictionary<TextSpan, bool>();

        var filteredDiagnostics = unmappedDiagnostics
            .Where(d =>
                !InRazorComment(d, sourceText, syntaxTree) &&
                !InCSharpLiteral(d, sourceText, syntaxTree) &&
                !InAttributeContainingCSharp(d, sourceText, syntaxTree, processedAttributes) &&
                !AppliesToTagHelperTagName(d, sourceText, syntaxTree) &&
                !ShouldFilterHtmlDiagnosticBasedOnErrorCode(d, sourceText, syntaxTree))
            .ToArray();

        return filteredDiagnostics;
    }

    private LspDiagnostic[] MapDiagnostics(
        RazorLanguageKind languageKind,
        LspDiagnostic[] diagnostics,
        IDocumentSnapshot documentSnapshot,
        RazorCodeDocument codeDocument)
    {
        var projects = RazorDiagnosticHelper.GetProjectInformation(documentSnapshot);
        using var mappedDiagnostics = new PooledArrayBuilder<LspDiagnostic>();

        foreach (var diagnostic in diagnostics)
        {
            // C# requests don't map directly to where they are in the document.
            if (languageKind == RazorLanguageKind.CSharp)
            {
                if (!TryGetOriginalDiagnosticRange(diagnostic, codeDocument, out var originalRange))
                {
                    continue;
                }

                diagnostic.Range = originalRange;
            }

            if (diagnostic is VSDiagnostic vsDiagnostic)
            {
                // We're the ones reporting the diagnostic, and it shows up as coming from our filename (not the generated one), so
                // the project info should be consistent too
                vsDiagnostic.Projects = projects;
            }

            mappedDiagnostics.Add(diagnostic);
        }

        return mappedDiagnostics.ToArray();
    }

    private static bool InRazorComment(
        LspDiagnostic d,
        SourceText sourceText,
        RazorSyntaxTree syntaxTree)
    {
        // If the diagnostic is within a Razor comment block, we don't want to show it.
        // Razor comments are not part of the Html document, so diagnostics within them stem from misinterpretation
        // of the "~" and comments that are generated by the compiler.
        return d.Range is not null &&
            syntaxTree.Root.FindNode(sourceText.GetTextSpan(d.Range), getInnermostNodeForTie: true) is RazorCommentBlockSyntax;
    }

    private static bool InCSharpLiteral(
        LspDiagnostic d,
        SourceText sourceText,
        RazorSyntaxTree syntaxTree)
    {
        if (d.Range is null)
        {
            return false;
        }

        var owner = syntaxTree.Root.FindNode(sourceText.GetTextSpan(d.Range), getInnermostNodeForTie: true);
        if (IsCsharpKind(owner))
        {
            return true;
        }

        if (owner is CSharpImplicitExpressionSyntax implicitExpressionSyntax &&
            implicitExpressionSyntax.Body is CSharpImplicitExpressionBodySyntax bodySyntax &&
            bodySyntax.CSharpCode is CSharpCodeBlockSyntax codeBlock)
        {
            return codeBlock.Children.Count == 1
                && IsCsharpKind(codeBlock.Children[0]);
        }

        return false;

        static bool IsCsharpKind([NotNullWhen(true)] SyntaxNode? node)
            => node?.Kind is SyntaxKind.CSharpExpressionLiteral
                or SyntaxKind.CSharpStatementLiteral
                or SyntaxKind.CSharpEphemeralTextLiteral;
    }

    private static bool AppliesToTagHelperTagName(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
    {
        // Goal of this method is to filter diagnostics that touch TagHelper tag names. Reason being is TagHelpers can output anything. Meaning
        // If you have a TagHelper like:
        //
        // <Input>
        // </Input>
        //
        // HTML would see this as an error because the input element can't have a body; however, a TagHelper could respect this in a totally valid
        // way.

        if (diagnostic.Range is null)
        {
            return false;
        }

        var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.End);

        var startOrEndTag = owner?.FirstAncestorOrSelf<RazorSyntaxNode>(static n => n is MarkupTagHelperStartTagSyntax || n is MarkupTagHelperEndTagSyntax);
        if (startOrEndTag is null)
        {
            return false;
        }

        var tagName = startOrEndTag is MarkupTagHelperStartTagSyntax startTag ? startTag.Name : ((MarkupTagHelperEndTagSyntax)startOrEndTag).Name;
        var tagNameRange = tagName.GetRange(syntaxTree.Source);

        if (!tagNameRange.IntersectsOrTouches(diagnostic.Range))
        {
            // The diagnostic doesn't touch the tag name
            return false;
        }

        // Diagnostic is touching the start or end tag name range
        return true;
    }

    private static bool ShouldFilterHtmlDiagnosticBasedOnErrorCode(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
    {
        if (!diagnostic.Code.HasValue)
        {
            return false;
        }

        diagnostic.Code.Value.TryGetSecond(out var str);

        return str switch
        {
            CSSErrorCodes.UnrecognizedBlockType => IsEscapedAtSign(diagnostic, sourceText),
            CSSErrorCodes.MissingOpeningBrace => IsCSharpInStyleBlock(diagnostic, sourceText, syntaxTree),
            CSSErrorCodes.MissingSelectorAfterCombinator => IsCSharpInStyleBlock(diagnostic, sourceText, syntaxTree),
            CSSErrorCodes.MissingSelectorBeforeCombinatorCode => IsCSharpInStyleBlock(diagnostic, sourceText, syntaxTree),
            HtmlErrorCodes.UnexpectedEndTagErrorCode => IsHtmlWithBangAndMatchingTags(diagnostic, sourceText, syntaxTree),
            HtmlErrorCodes.InvalidNestingErrorCode => IsAnyFilteredInvalidNestingError(diagnostic, sourceText, syntaxTree),
            HtmlErrorCodes.MissingEndTagErrorCode => syntaxTree.Options.FileKind.IsComponent(), // Redundant with RZ9980 in Components
            HtmlErrorCodes.TooFewElementsErrorCode => IsAnyFilteredTooFewElementsError(diagnostic, sourceText, syntaxTree),
            _ => false,
        };

        static bool IsEscapedAtSign(LspDiagnostic diagnostic, SourceText sourceText)
        {
            // Filters out "Unrecognized block type" errors in CSS, which occur with something like this:
            //
            // <style>
            //     @@font - face
            //     {
            //         // contents
            //     }
            // </style>
            //
            // The "@@" tells Razor that the user wants an "@" in the final Html, but the design time document
            // for the Html has to line up with the source Razor file, so that doesn't happen in the IDE. When
            // CSS gets the two "@"s, it raises the "Unrecognized block type" error.

            if (!sourceText.TryGetAbsoluteIndex(diagnostic.Range.Start, out var absoluteIndex))
            {
                return false;
            }

            // It's much easier to just check the source text directly, rather than try to understand all of the
            // possible shapes of the syntax tree here. We assume that since the diagnostics we're filtering out
            // came from the CSS server, it's a CSS block.
            return absoluteIndex > 0 &&
                sourceText[absoluteIndex] == '@' &&
                sourceText[absoluteIndex - 1] == '@';
        }

        static bool IsCSharpInStyleBlock(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
        {
            // C# in a style block causes diagnostics because the HTML background document replaces C# with "~"
            var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.Start);
            if (owner is null)
            {
                return false;
            }

            var element = owner.FirstAncestorOrSelf<MarkupElementSyntax>(static n => n.StartTag?.Name.Content == "style");
            var csharp = owner.FirstAncestorOrSelf<CSharpCodeBlockSyntax>();

            return csharp is not null ||
                (element?.Body.Any(static c => c is CSharpCodeBlockSyntax) ?? false);
        }

        // Ideally this would be solved instead by not emitting the "!" at the HTML backing file,
        // but we don't currently have a system to accomplish that
        static bool IsAnyFilteredTooFewElementsError(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
        {
            var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.Start);
            if (owner is null)
            {
                return false;
            }

            var element = owner.FirstAncestorOrSelf<MarkupElementSyntax>();
            if (element is null)
            {
                return false;
            }

            if (element.StartTag?.Name.Content != "html")
            {
                return false;
            }

            var bodyElement = element
                .ChildNodes()
                .OfType<MarkupElementSyntax>()
                .SingleOrDefault(static element => element.StartTag?.Name.Content == "body");

            return bodyElement is not null &&
                   bodyElement.StartTag?.Bang is not null;
        }

        // Ideally this would be solved instead by not emitting the "!" at the HTML backing file,
        // but we don't currently have a system to accomplish that
        static bool IsHtmlWithBangAndMatchingTags(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
        {
            var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.Start);
            if (owner is null)
            {
                return false;
            }

            var element = owner.FirstAncestorOrSelf<MarkupElementSyntax>();
            var startNode = element?.StartTag;
            var endNode = element?.EndTag;

            if (startNode is null || endNode is null)
            {
                // We only care about tags with a start and an end because we want to exclude diagnostics from their children
                return false;
            }

            var haveBang = startNode.Bang.IsValid() && endNode.Bang.IsValid();
            var namesEquivalent = startNode.Name.Content == endNode.Name.Content;

            return haveBang && namesEquivalent;
        }

        static bool IsAnyFilteredInvalidNestingError(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
            => IsInvalidNestingWarningWithinComponent(diagnostic, sourceText, syntaxTree) ||
               IsInvalidNestingFromBody(diagnostic, sourceText, syntaxTree);

        static bool IsInvalidNestingWarningWithinComponent(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
        {
            var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.Start);
            if (owner is null)
            {
                return false;
            }

            var taghelperNode = owner.FirstAncestorOrSelf<MarkupTagHelperElementSyntax>();

            return taghelperNode is not null;
        }

        // Ideally this would be solved instead by not emitting the "!" at the HTML backing file,
        // but we don't currently have a system to accomplish that
        static bool IsInvalidNestingFromBody(LspDiagnostic diagnostic, SourceText sourceText, RazorSyntaxTree syntaxTree)
        {
            var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.Start);
            if (owner is null)
            {
                return false;
            }

            var body = owner.FirstAncestorOrSelf<MarkupElementSyntax>(static n => n.StartTag?.Name.Content.Equals("body", StringComparison.Ordinal) == true);

            if (ReferenceEquals(body, owner))
            {
                return false;
            }

            if (diagnostic.Message is null)
            {
                return false;
            }

            return diagnostic.Message.EndsWith("cannot be nested inside element 'html'.") && body?.StartTag?.Bang is not null;
        }
    }

    private static bool InAttributeContainingCSharp(
        LspDiagnostic diagnostic,
        SourceText sourceText,
        RazorSyntaxTree syntaxTree,
        Dictionary<TextSpan, bool> processedAttributes)
    {
        // Examine the _end_ of the diagnostic to see if we're at the
        // start of an (im/ex)plicit expression. Looking at the start
        // of the diagnostic isn't sufficient.
        if (diagnostic.Range is null)
        {
            return false;
        }

        var owner = syntaxTree.FindInnermostNode(sourceText, diagnostic.Range.End);
        if (owner is null)
        {
            return false;
        }

        var markupAttributeNode = owner.FirstAncestorOrSelf<RazorSyntaxNode>(static n =>
            n is MarkupAttributeBlockSyntax ||
            n is MarkupTagHelperAttributeSyntax ||
            n is MarkupMiscAttributeContentSyntax);

        if (markupAttributeNode is not null)
        {
            if (!processedAttributes.TryGetValue(markupAttributeNode.Span, out var doesAttributeContainNonMarkup))
            {
                doesAttributeContainNonMarkup = CheckIfAttributeContainsNonMarkupNodes(markupAttributeNode);
                processedAttributes.Add(markupAttributeNode.Span, doesAttributeContainNonMarkup);
            }

            return doesAttributeContainNonMarkup;
        }

        return false;

        static bool CheckIfAttributeContainsNonMarkupNodes(RazorSyntaxNode attributeNode)
        {
            // Only allow markup, generic & (non-razor comment) token nodes
            var containsNonMarkupNodes = attributeNode.DescendantNodesAndTokens().Any(IsNotMarkupNodeOrCommentToken);

            return containsNonMarkupNodes;
        }

        static bool IsNotMarkupNodeOrCommentToken(RazorSyntaxNodeOrToken nodeOrToken)
        {
            return !(nodeOrToken.IsToken && nodeOrToken.AsToken().Kind == SyntaxKind.RazorCommentTransition) &&
                   !(nodeOrToken.IsNode && nodeOrToken.AsNode() is MarkupBlockSyntax or MarkupSyntaxNode or GenericBlockSyntax);
        }
    }

    private bool ShouldFilterCSharpDiagnosticBasedOnErrorCode(LspDiagnostic diagnostic, RazorCodeDocument codeDocument)
    {
        if (diagnostic.Code is not { } code ||
            !code.TryGetSecond(out var str) ||
            str is null)
        {
            return false;
        }

        return str switch
        {
            "CS1525" => ShouldIgnoreCS1525(diagnostic, codeDocument),
            _ => s_cSharpDiagnosticsToIgnore.Contains(str) &&
                diagnostic.Severity != LspDiagnosticSeverity.Error
        };

        bool ShouldIgnoreCS1525(LspDiagnostic diagnostic, RazorCodeDocument codeDocument)
        {
            if (CheckIfDocumentHasRazorDiagnostic(codeDocument, RazorDiagnosticFactory.TagHelper_EmptyBoundAttribute.Id) &&
                TryGetOriginalDiagnosticRange(diagnostic, codeDocument, out var originalRange) &&
                originalRange.IsUndefined())
            {
                // Empty attribute values will take the following form in the generated C# document:
                // __o = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.ProgressEventArgs>(this, );
                // The trailing `)` with no value preceding it, will lead to a C# error which doesn't make sense within the razor file.
                // The empty attribute value is not directly mappable to Razor, hence we check if the diagnostic has an undefined range.
                // Note; Error RZ2008 informs the user that the empty attribute value is not allowed.
                // https://github.com/dotnet/aspnetcore/issues/30480
                return true;
            }

            return false;
        }
    }

    private static bool CheckIfDocumentHasRazorDiagnostic(RazorCodeDocument codeDocument, string razorDiagnosticCode)
    {
        return codeDocument.GetRequiredSyntaxTree().Diagnostics.Any(razorDiagnosticCode, static (d, code) => d.Id == code);
    }

    private bool TryGetOriginalDiagnosticRange(LspDiagnostic diagnostic, RazorCodeDocument codeDocument, [NotNullWhen(true)] out LspRange? originalRange)
    {
        if (IsRudeEditDiagnostic(diagnostic))
        {
            if (TryRemapRudeEditRange(diagnostic.Range, codeDocument, out originalRange))
            {
                return true;
            }

            return false;
        }

        if (!_documentMappingService.TryMapToRazorDocumentRange(
            codeDocument.GetRequiredCSharpDocument(),
            diagnostic.Range,
            MappingBehavior.Inferred,
            out originalRange))
        {
            // Couldn't remap the range correctly.
            // If this is error it's worth at least logging so we know if there's an issue
            // for mapping when a user reports not seeing an error they thought they should
            if (diagnostic.Severity == LspDiagnosticSeverity.Error)
            {
                _logger.LogWarning($"Dropping diagnostic {diagnostic.Code}:{diagnostic.Message} at csharp range {diagnostic.Range}");
            }

            return false;
        }

        return true;
    }

    private static bool IsRudeEditDiagnostic(LspDiagnostic diagnostic)
    {
        return diagnostic.Code.HasValue &&
            diagnostic.Code.Value.TryGetSecond(out var str) &&
            str.StartsWith("ENC");
    }

    private bool TryRemapRudeEditRange(LspRange diagnosticRange, RazorCodeDocument codeDocument, [NotNullWhen(true)] out LspRange? remappedRange)
    {
        // This is a rude edit diagnostic that has already been mapped to the Razor document. The mapping isn't absolutely correct though,
        // it's based on the runtime code generation of the Razor document therefore we need to re-map the already mapped diagnostic in a
        // semi-intelligent way.

        var syntaxRoot = codeDocument.GetRequiredSyntaxRoot();
        var sourceText = codeDocument.Source.Text;
        var span = sourceText.GetTextSpan(diagnosticRange);
        var owner = syntaxRoot.FindNode(span, getInnermostNodeForTie: true);

        switch (owner?.Kind)
        {
            case SyntaxKind.CSharpStatementLiteral: // Simple C# in @code block, @{ ... } etc.
            case SyntaxKind.CSharpExpressionLiteral: // Referenced simple C# in an implicit expression @Foo((abc) => {....})
                // Good as is, we were able to find a known leaf-node that fully contains the diagnostic range. Therefore we can
                // return the diagnostic range as is.
                remappedRange = diagnosticRange;
                return true;

            default:
                // Unsupported owner of rude diagnostic, lets map to the entirety of the diagnostic range to be sure the diagnostic can be presented

                _logger.LogInformation($"Failed to remap rude edit for SyntaxTree owner '{owner?.Kind}'.");

                var startLineIndex = diagnosticRange.Start.Line;
                if (startLineIndex >= sourceText.Lines.Count)
                {
                    // Documents aren't sync'd we can't remap the ranges correctly, drop the diagnostic.
                    remappedRange = null;
                    return false;
                }

                var startLine = sourceText.Lines[startLineIndex];

                // Look for the first non-whitespace character so we're not squiggling random whitespace at the start of the diagnostic
                var diagnosticStartCharacter = sourceText.TryGetFirstNonWhitespaceOffset(startLine.Span, out var firstNonWhitespaceOffset)
                    ? firstNonWhitespaceOffset
                    : 0;
                var startLinePosition = (startLineIndex, diagnosticStartCharacter);

                var endLineIndex = diagnosticRange.End.Line;
                if (endLineIndex >= sourceText.Lines.Count)
                {
                    // Documents aren't sync'd we can't remap the ranges correctly, drop the diagnostic.
                    remappedRange = null;
                    return false;
                }

                var endLine = sourceText.Lines[endLineIndex];

                // Look for the last non-whitespace character so we're not squiggling random whitespace at the end of the diagnostic
                var diagnosticEndCharacter = sourceText.TryGetLastNonWhitespaceOffset(endLine.Span, out var lastNonWhitespaceOffset)
                    ? lastNonWhitespaceOffset
                    : 0;
                var diagnosticEndWhitespaceOffset = diagnosticEndCharacter + 1;
                var endLinePosition = (endLineIndex, diagnosticEndWhitespaceOffset);

                remappedRange = LspFactory.CreateRange(startLinePosition, endLinePosition);
                return true;
        }
    }
}
