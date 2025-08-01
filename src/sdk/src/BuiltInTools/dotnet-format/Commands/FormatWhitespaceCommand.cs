﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using static Microsoft.CodeAnalysis.Tools.FormatCommandCommon;

namespace Microsoft.CodeAnalysis.Tools.Commands
{
    internal static class FormatWhitespaceCommand
    {
        private static readonly FormatWhitespaceHandler s_formattingHandler = new();

        internal static Command GetCommand()
        {
            var command = new Command("whitespace", Resources.Run_whitespace_formatting)
            {
                FolderOption
            };
            command.AddCommonOptions();
            command.Validators.Add(EnsureFolderNotSpecifiedWithNoRestore);
            command.Validators.Add(EnsureFolderNotSpecifiedWhenLoggingBinlog);
            command.Action = s_formattingHandler;
            return command;
        }

        internal static void EnsureFolderNotSpecifiedWithNoRestore(CommandResult symbolResult)
        {
            var folder = symbolResult.GetValue(FolderOption);
            var noRestore = symbolResult.GetValue(NoRestoreOption);
            if (folder && noRestore)
            {
                symbolResult.AddError(Resources.Cannot_specify_the_folder_option_with_no_restore);
            }
        }

        internal static void EnsureFolderNotSpecifiedWhenLoggingBinlog(CommandResult symbolResult)
        {
            var folder = symbolResult.GetValue(FolderOption);
            var binarylog = symbolResult.GetResult(BinarylogOption);
            if (folder && binarylog is not null && !binarylog.Implicit)
            {
                symbolResult.AddError(Resources.Cannot_specify_the_folder_option_when_writing_a_binary_log);
            }
        }

        private class FormatWhitespaceHandler : AsynchronousCommandLineAction
        {
            public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
            {
                var formatOptions = parseResult.ParseVerbosityOption(FormatOptions.Instance);
                var logger = SetupLogging(minimalLogLevel: formatOptions.LogLevel, minimalErrorLevel: LogLevel.Warning);
                formatOptions = parseResult.ParseCommonOptions(formatOptions, logger);
                formatOptions = parseResult.ParseWorkspaceOptions(formatOptions);

                formatOptions = formatOptions with { FixCategory = FixCategory.Whitespace };

                return await FormatAsync(formatOptions, logger, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
