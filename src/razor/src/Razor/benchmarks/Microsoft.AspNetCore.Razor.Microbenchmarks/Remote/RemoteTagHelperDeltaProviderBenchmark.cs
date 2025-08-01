﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Remote.Razor;

namespace Microsoft.AspNetCore.Razor.Microbenchmarks;

public class RemoteTagHelperDeltaProviderBenchmark
{
    public RemoteTagHelperDeltaProviderBenchmark()
    {
        DefaultTagHelperSet = CommonResources.LegacyTagHelpers.ToHashSet().ToImmutableArray();

        Added50PercentMoreDefaultTagHelpers = DefaultTagHelperSet
            .Take(DefaultTagHelperSet.Length / 2)
            .Select(th => th.WithName(th.Name + "Added"))
            .Concat(DefaultTagHelperSet)
            .ToHashSet()
            .ToImmutableArray();

        RemovedHalfOfDefaultTagHelpers = DefaultTagHelperSet
            .Take(CommonResources.LegacyTagHelpers.Length / 2)
            .ToHashSet()
            .ToImmutableArray();

        var tagHelpersToMutate = DefaultTagHelperSet
            .Take(2)
            .Select(th => th.WithName(th.Name + "Mutated"));
        MutatedTwoDefaultTagHelpers = DefaultTagHelperSet
            .Skip(2)
            .Concat(tagHelpersToMutate)
            .ToHashSet()
            .ToImmutableArray();

        DefaultTagHelperChecksumsSet = DefaultTagHelperSet.SelectAsArray(t => t.Checksum);
        Added50PercentMoreDefaultTagHelpersChecksums = Added50PercentMoreDefaultTagHelpers.SelectAsArray(t => t.Checksum);
        RemovedHalfOfDefaultTagHelpersChecksums = RemovedHalfOfDefaultTagHelpers.SelectAsArray(t => t.Checksum);
        MutatedTwoDefaultTagHelpersChecksums = MutatedTwoDefaultTagHelpers.SelectAsArray(t => t.Checksum);

        ProjectId = ProjectId.CreateNewId();
    }

    private ImmutableArray<TagHelperDescriptor> DefaultTagHelperSet { get; }
    private ImmutableArray<Checksum> DefaultTagHelperChecksumsSet { get; }
    private ImmutableArray<TagHelperDescriptor> Added50PercentMoreDefaultTagHelpers { get; }
    private ImmutableArray<Checksum> Added50PercentMoreDefaultTagHelpersChecksums { get; }
    private ImmutableArray<TagHelperDescriptor> RemovedHalfOfDefaultTagHelpers { get; }
    private ImmutableArray<Checksum> RemovedHalfOfDefaultTagHelpersChecksums { get; }
    private ImmutableArray<TagHelperDescriptor> MutatedTwoDefaultTagHelpers { get; }
    private ImmutableArray<Checksum> MutatedTwoDefaultTagHelpersChecksums { get; }
    private ProjectId ProjectId { get; }

    [AllowNull]
    private RemoteTagHelperDeltaProvider Provider { get; set; }

    private int LastResultId { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        Provider = new RemoteTagHelperDeltaProvider();
        var delta = Provider.GetTagHelpersDelta(ProjectId, lastResultId: -1, DefaultTagHelperChecksumsSet);
        LastResultId = delta.ResultId;
    }

    [Benchmark(Description = "Calculate Delta - New project")]
    public void TagHelper_GetTagHelpersDelta_NewProject()
    {
        var projectId = ProjectId.CreateNewId();
        _ = Provider.GetTagHelpersDelta(projectId, lastResultId: -1, DefaultTagHelperChecksumsSet);
    }

    [Benchmark(Description = "Calculate Delta - Remove project")]
    public void TagHelper_GetTagHelpersDelta_RemoveProject()
    {
        _ = Provider.GetTagHelpersDelta(ProjectId, LastResultId, ImmutableArray<Checksum>.Empty);
    }

    [Benchmark(Description = "Calculate Delta - Add lots of TagHelpers")]
    public void TagHelper_GetTagHelpersDelta_AddLots()
    {
        _ = Provider.GetTagHelpersDelta(ProjectId, LastResultId, Added50PercentMoreDefaultTagHelpersChecksums);
    }

    [Benchmark(Description = "Calculate Delta - Remove lots of TagHelpers")]
    public void TagHelper_GetTagHelpersDelta_RemoveLots()
    {
        _ = Provider.GetTagHelpersDelta(ProjectId, LastResultId, RemovedHalfOfDefaultTagHelpersChecksums);
    }

    [Benchmark(Description = "Calculate Delta - Mutate two TagHelpers")]
    public void TagHelper_GetTagHelpersDelta_Mutate2()
    {
        _ = Provider.GetTagHelpersDelta(ProjectId, LastResultId, MutatedTwoDefaultTagHelpersChecksums);
    }

    [Benchmark(Description = "Calculate Delta - No change")]
    public void TagHelper_GetTagHelpersDelta_NoChange()
    {
        _ = Provider.GetTagHelpersDelta(ProjectId, LastResultId, DefaultTagHelperChecksumsSet);
    }
}
