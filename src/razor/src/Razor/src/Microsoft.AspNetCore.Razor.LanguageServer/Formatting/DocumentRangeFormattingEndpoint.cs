﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.EndpointContracts;
using Microsoft.AspNetCore.Razor.LanguageServer.Hosting;
using Microsoft.CodeAnalysis.Razor.Formatting;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Formatting;

[RazorLanguageServerEndpoint(Methods.TextDocumentRangeFormattingName)]
internal class DocumentRangeFormattingEndpoint(
    IRazorFormattingService razorFormattingService,
    IHtmlFormatter htmlFormatter,
    RazorLSPOptionsMonitor optionsMonitor) : IRazorRequestHandler<DocumentRangeFormattingParams, TextEdit[]?>, ICapabilitiesProvider
{
    private readonly IRazorFormattingService _razorFormattingService = razorFormattingService;
    private readonly RazorLSPOptionsMonitor _optionsMonitor = optionsMonitor;
    private readonly IHtmlFormatter _htmlFormatter = htmlFormatter;

    public bool MutatesSolutionState => false;

    public void ApplyCapabilities(VSInternalServerCapabilities serverCapabilities, VSInternalClientCapabilities clientCapabilities)
    {
        serverCapabilities.DocumentRangeFormattingProvider = new DocumentRangeFormattingOptions();
    }

    public TextDocumentIdentifier GetTextDocumentIdentifier(DocumentRangeFormattingParams request)
    {
        return request.TextDocument;
    }

    public async Task<TextEdit[]?> HandleRequestAsync(DocumentRangeFormattingParams request, RazorRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (!_optionsMonitor.CurrentValue.Formatting.IsEnabled())
        {
            return null;
        }

        var documentContext = requestContext.DocumentContext;
        if (documentContext is null || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var codeDocument = await documentContext.GetCodeDocumentAsync(cancellationToken).ConfigureAwait(false);

        if (request.Options.OtherOptions is not null &&
            request.Options.OtherOptions.TryGetValue("fromPaste", out var fromPasteObj) &&
            fromPasteObj.TryGetFirst(out var fromPaste))
        {
            if (fromPaste && !_optionsMonitor.CurrentValue.Formatting.IsOnPasteEnabled())
            {
                return null;
            }
        }

        var options = RazorFormattingOptions.From(request.Options, _optionsMonitor.CurrentValue.CodeBlockBraceOnNextLine);

        if (await _htmlFormatter.GetDocumentFormattingEditsAsync(
            documentContext.Snapshot,
            documentContext.Uri,
            request.Options,
            cancellationToken).ConfigureAwait(false) is not { } htmlChanges)
        {
            return null;
        }

        var changes = await _razorFormattingService.GetDocumentFormattingChangesAsync(documentContext, htmlChanges, request.Range.ToLinePositionSpan(), options, cancellationToken).ConfigureAwait(false);

        return [.. changes.Select(codeDocument.Source.Text.GetTextEdit)];
    }
}
