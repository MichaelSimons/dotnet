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

        private readonly Dictionary<string, PackageInfo> _sbrpPackages = [];

        public override bool Execute()
        {
            ReadSbrpPackages("referencePackages");
            ReadSbrpPackages("textOnlyPackages");

            return !Log.HasLoggedErrors;
        }

        private string GetSBRPPackagesPath(string packageType) => Path.Combine(SbrpRepoPath, "src", packageType, "src");

        private void ReadSbrpPackages(string packageType)
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true };

            foreach (string projectPath in Directory.GetFiles(GetSBRPPackagesPath(packageType), "*.csproj", options))
            {
                XDocument xmlDoc = XDocument.Load(projectPath);
                IEnumerable<string> tfms = xmlDoc.Element("Project")?
                    .Elements("PropertyGroup")
                    .Elements("TargetFrameworks")
                    .FirstOrDefault()?.Value?.Split(';');

                if (tfms == null || !tfms.Any())
                {
                    Log.LogError($"No TargetFrameworks were delected in {projectPath}.");
                }

                string version = Directory.GetParent(projectPath).Name;
                string projectName = Path.GetFileNameWithoutExtension(projectPath);
                PackageInfo info = new()
                {
                    Version = version,
                    Name = projectName[..(projectName.Length - 1 - version.Length)],
                    Type = packageType,
                    Tfms = new HashSet<string>(tfms),
                };

                _sbrpPackages.Add($"{info.Id}", info);
                Log.LogMessage($"Detected SBRP Package: {info.Id} {packageType}");
            }
        }

        private class PackageInfo
        {
            public string Id => $"{Name}/{Version}";
            public string Name { get; set; }
            public string Version { get; set; }
            public string Type{ get; set; }
            public HashSet<string> Tfms { get; set; }
            public HashSet<string> References { get; } = [];
            public HashSet<string> ReferencedTfms { get; } = [];
        }
    }
}