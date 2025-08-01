﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.KeywordHighlighting.KeywordHighlighters;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.KeywordHighlighting;

[Trait(Traits.Feature, Traits.Features.KeywordHighlighting)]
public sealed class YieldStatementHighlighterTests : AbstractCSharpKeywordHighlighterTests
{
    internal override Type GetHighlighterType()
        => typeof(YieldStatementHighlighter);

    [Fact]
    public Task TestExample1_1()
        => TestAsync(
            """
            class C
            {
                IEnumerable<int> Range(int min, int max)
                {
                    while (true)
                    {
                        if (min >= max)
                        {
                            {|Cursor:[|yield break|];|}
                        }

                        [|yield return|] min++;
                    }
                }
            }
            """);

    [Fact]
    public Task TestExample1_2()
        => TestAsync(
            """
            class C
            {
                IEnumerable<int> Range(int min, int max)
                {
                    while (true)
                    {
                        if (min >= max)
                        {
                            [|yield break|];
                        }

                        {|Cursor:[|yield return|]|} min++;
                    }
                }
            }
            """);

    [Fact]
    public Task TestExample1_3()
        => TestAsync(
            """
            class C
            {
                IEnumerable<int> Range(int min, int max)
                {
                    while (true)
                    {
                        if (min >= max)
                        {
                            yield break;
                        }

                        yield return {|Cursor:min++|};
                    }
                }
            }
            """);

    [Fact]
    public Task TestExample1_4()
        => TestAsync(
            """
            class C
            {
                IEnumerable<int> Range(int min, int max)
                {
                    while (true)
                    {
                        if (min >= max)
                        {
                            [|yield break|];
                        }

                        [|yield return|] min++;{|Cursor:|}
                    }
                }
            }
            """);
}
