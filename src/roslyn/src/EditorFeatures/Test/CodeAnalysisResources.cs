﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Resources;

namespace Microsoft.CodeAnalysis;

// This class exists as a way to load resources from the Microsoft.CodeAnalysis.CodeAnalysisResources class from
// the Microsoft.CodeAnalysis assembly.  Microsoft.CodeAnalysis.CodeAnalysisResources is internal but we can't add
// InternalsVisibleTo(this-assembly) because there are numerous shared (linked) files common to both
// Microsoft.CodeAnalysis and Microsoft.CodeAnalysis.Workspaces and that gives us major issues with duplicate 
// internal types that suddenly become visible (e.g., SpecializedCollections) and that leads down a rabbit hole
// of requiring assembly aliasing that would make many tests in this project unreadable.  The decision was made to
// manually load the few resources we need from the CodeAnalysis assembly at the cost of Find All References and
// Rename not working as expected.
internal static class CodeAnalysisResources
{
    public static string InMemoryAssembly => GetString("InMemoryAssembly");

    private static ResourceManager s_codeAnalysisResourceManager;

    private static string GetString(string resourceName)
    {
        s_codeAnalysisResourceManager ??= new ResourceManager(typeof(CodeAnalysisResources).FullName, typeof(Compilation).Assembly);

        return s_codeAnalysisResourceManager.GetString(resourceName);
    }
}
