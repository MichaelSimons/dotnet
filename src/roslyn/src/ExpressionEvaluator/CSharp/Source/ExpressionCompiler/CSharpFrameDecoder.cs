﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE;
using Microsoft.CodeAnalysis.ExpressionEvaluator;

namespace Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator
{
    [DkmReportNonFatalWatsonException(ExcludeExceptionType = typeof(NotImplementedException)), DkmContinueCorruptingException]
    internal sealed class CSharpFrameDecoder : FrameDecoder<CSharpCompilation, MethodSymbol, PEModuleSymbol, TypeSymbol, TypeParameterSymbol>
    {
        public CSharpFrameDecoder()
            : base(CSharpInstructionDecoder.Instance)
        {
        }
    }
}
