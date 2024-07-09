// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.UnifiedBuild.Tasks
{
    /// <summary>
    /// Scans the source-build-reference-packages repo for various options:
    /// 1. Unreferenced packages
    /// 2. Unreferenced TFMs
    /// 3. Usage of specified packages
    /// 4. Usage of specified TFMS
    /// </summary>
    public class ScanForUnreferencedSbrps : Task
    {
        /// <summary>
        /// Path to the SBRP repo to scan.
        /// </summary>
        [Required]
        public string SbrpRepoPath { get; set; }

        private Dictionary<string, PackageInfo> _sbrpPackages = new();

        public override bool Execute()
        {
            IList<string> filesWithoutPDBs = GenerateSymbolsLayout(IndexAllSymbols());
            if (filesWithoutPDBs.Count > 0)
            {
                LogErrorOrWarning(FailOnMissingPDBs, $"Did not find PDBs for the following SDK files:");
                foreach (string file in filesWithoutPDBs)
                {
                    LogErrorOrWarning(FailOnMissingPDBs, file);
                }
           }

            return !Log.HasLoggedErrors;
        }

        private void ReadSbrpPackages()
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true};
            
            foreach (string projectFile in Directory.GetFiles(SbrpRepoPath, "*.csproj", options))
            {
                XDocument xmlDoc = XDocument.Load(projectFile);
                IEnumerable<string> tfms = xmlDoc.Element("Project")?
                    .Elements("PropertyGroup")
                    .Elements("TargetFrameworks")
                    .FirstOrDefault()?.Value?.Split(';');

                if (tfms == null || !tfms.Any())
                {
                     Log.LogError($"No TargetFrameworks were delected in {projectFile}.");
                }

                string version = Directory.GetParent(projectFile).Name;
                string projectName = Path.GetFileNameWithoutExtension(projectFile);
                PackageInfo info = new ()
                {
                    Version = version,
                    Name = projectName.Substring(0, projectName.Length - 1 - version.Length),
                    TFMs = new HashSet<string>(tfms),
                };

                sbrps.Add($"{info.Id}", info);
            }
        }

        private class PackageInfo
        {
            public string Id => $"{Name}/{Version}";
            public string Name {get; set;}
            public string Version {get; set;}
            public HashSet<string> Tfms {get; set; }
            public HashSet<string> References { get; } = new ();
            public HashSet<string> ReferencedTfms { get; } = new ();
        }
    }
}