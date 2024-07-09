// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.DotNet.UnifiedBuild.Tasks
{
    /// <summary>
    /// Reports the usage of the source-build-reference-packages:
    /// 1. Unreferenced packages
    /// 2. Unreferenced TFMs
    /// </summary>
    public class WriteSbrpUsageReport : Task
    {
        /// <summary>
        /// Path to the SBRP repo to scan.
        /// </summary>
        [Required]
        public string SbrpRepoPath { get; set; }

        private readonly Dictionary<string, PackageInfo> _sbrpPackages = [];

        public override bool Execute()
        {
            Log.LogMessage($"Scanning for SBRP Package Usage...");

            ReadSbrpPackages("referencePackages", trackTfms: true);
            ReadSbrpPackages("textOnlyPackages", trackTfms: false);

            return !Log.HasLoggedErrors;
        }

        private string GetSBRPPackagesPath(string packageType) => Path.Combine(SbrpRepoPath, "src", packageType, "src");

        private void ReadSbrpPackages(string packageType, bool trackTfms)
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true };

            foreach (string projectPath in Directory.GetFiles(GetSBRPPackagesPath(packageType), "*.csproj", options))
            {
                DirectoryInfo directory = Directory.GetParent(projectPath);
                string version = directory.Name;
                string projectName = Path.GetFileNameWithoutExtension(projectPath);
                PackageInfo info = new()
                {
                    Version = version,
                    Name = projectName[..(projectName.Length - 1 - version.Length)],
                    Path = directory.FullName,
                };

                if (trackTfms)
                {
                    XDocument xmlDoc = XDocument.Load(projectPath);
                    // Reference packages are generated using the TargetFrameworks property
                    // so there is no need to handle the TargetFramework property.
                    string[] tfms = xmlDoc.Element("Project")?
                        .Elements("PropertyGroup")
                        .Elements("TargetFrameworks")
                        .FirstOrDefault()?.Value?.Split(';');

                    if (tfms == null || !tfms.Any())
                    {
                        Log.LogError($"No TargetFrameworks were delected in {projectPath}.");
                    }

                    info.Tfms = new HashSet<string>(tfms);
                }
                else
                {
                    info.Tfms = [];
                }

                _sbrpPackages.Add($"{info.Id}", info);
                Log.LogMessage($"Detected package: {info.Id});
            }
        }

        private class PackageInfo
        {
            public string Id => $"{Name}/{Version}";
            public string Name { get; set; }
            public string Version { get; set; }
            public string Path{ get; set; }
            public HashSet<string> Tfms { get; set; }
            public HashSet<string> References { get; } = [];
            public HashSet<string> ReferencedTfms { get; } = [];
        }
    }
}