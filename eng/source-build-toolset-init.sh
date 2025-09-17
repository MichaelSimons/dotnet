#!/usr/bin/env bash

### This script provides shared functionality for initializing the source build toolset.
### It handles custom SDK setup, MSBuild SDK resolver initialization, and toolset preparation.

set -euo pipefail

function source_only_toolset_init() {
    local scriptroot="$1"
    local custom_sdk_dir="${2:-}"
    local custom_packages_dir="${3:-}"
    local nuget_packages="${4:-$scriptroot/.packages/}"
    local binary_log="${5:-false}"
    local properties=("${@:6}")  # All remaining arguments are properties

    echo "Initializing source-only toolset..."

    # Set up custom SDK if specified
    if [ -n "$custom_sdk_dir" ]; then
        if [ ! -d "$custom_sdk_dir" ]; then
            echo "ERROR: Custom SDK directory '$custom_sdk_dir' does not exist"
            exit 1
        fi
        if [ ! -x "$custom_sdk_dir/dotnet" ]; then
            echo "ERROR: Custom SDK '$custom_sdk_dir/dotnet' does not exist or is not executable"
            exit 1
        fi
        export SDK_VERSION=$("$custom_sdk_dir/dotnet" --version)
        export CLI_ROOT="$custom_sdk_dir"
        echo "Using custom bootstrap SDK from '$CLI_ROOT', version '$SDK_VERSION'"

        # Set _InitializeDotNetCli & DOTNET_INSTALL_DIR so that eng/common/tools.sh doesn't attempt to restore the SDK.
        export _InitializeDotNetCli="$CLI_ROOT"
        export DOTNET_INSTALL_DIR="$CLI_ROOT"
    fi

    # Initialize build tool
    if [ -z "${_InitializeBuildTool:-}" ]; then
        # Source eng/common/tools.sh to get InitializeBuildTool
        if [ -f "$scriptroot/eng/common/tools.sh" ]; then
            source "$scriptroot/eng/common/tools.sh"
        else
            echo "ERROR: eng/common/tools.sh not found"
            exit 1
        fi
        InitializeBuildTool
    fi

    # Set up MSBuild SDK resolver
    if ! setup_msbuild_sdk_resolver "$scriptroot"; then
        echo "ERROR: Failed to set up MSBuild SDK resolver"
        exit 1
    fi

    # Set up binary log for init-source-only if needed
    local initSourceOnlyBinaryLog=""
    if [[ "$binary_log" == "true" ]]; then
        initSourceOnlyBinaryLog="/bl:\"$log_dir/init-source-only.binlog\""
        # Run init-source-only with binary log option
        MSBuild-Core "$scriptroot/eng/init-source-only.proj" "$initSourceOnlyBinaryLog" "${properties[@]}"
    fi

    # Get bootstrap Arcade directory
    local bootstrapArcadeDir=$(cat "$scriptroot/artifacts/toolset/bootstrap-sdks.txt" | grep "microsoft.dotnet.arcade.sdk")
    local arcadeBuildStepsDir="$bootstrapArcadeDir/tools/"

    # Set _InitializeToolset so that eng/common/tools.sh doesn't attempt to restore the arcade toolset again.
    export _InitializeToolset="${arcadeBuildStepsDir}/Build.proj"

    # Set up source-built SDK resolver as final step
    setup_source_built_sdk_resolver "$scriptroot" "$custom_packages_dir" "$nuget_packages"

    echo "Source-only toolset initialization complete"
}

function setup_source_built_sdk_resolver() {
    local scriptroot="$1"
    local custom_packages_dir="${2:-}"
    local nuget_packages="${3:-$scriptroot/.packages/}"

    echo "Setting up source-built SDK resolver..."

    # Set up NuGet packages directory if not already set
    if [[ -z "${NUGET_PACKAGES:-}" ]]; then
        export NUGET_PACKAGES="$nuget_packages"
    fi

    echo "NuGet packages cache: '$NUGET_PACKAGES'"

    # Find PackageVersions.props path
    local packageVersionsPath=''
    local packagesDir="$scriptroot/prereqs/packages/"
    local packagesArchiveDir="${packagesDir}archive/"
    local packagesPreviouslySourceBuiltDir="${packagesDir}previously-source-built/"
    local tempDir=""

    if [[ -n "$custom_packages_dir" && -f "$custom_packages_dir/PackageVersions.props" ]]; then
        packageVersionsPath="$custom_packages_dir/PackageVersions.props"
        echo "Using custom PackageVersions.props from: $packageVersionsPath"
    elif [ -d "$packagesArchiveDir" ]; then
        # Check if already extracted
        if [ -f "${packagesPreviouslySourceBuiltDir}PackageVersions.props" ]; then
            packageVersionsPath="${packagesPreviouslySourceBuiltDir}PackageVersions.props"
            echo "Using extracted PackageVersions.props from: $packageVersionsPath"
        else
            # Try to extract from archive
            local sourceBuiltArchive=$(find "$packagesArchiveDir" -maxdepth 1 -name 'Private.SourceBuilt.Artifacts*.tar.gz' | head -n 1)
            if [ -f "$sourceBuiltArchive" ]; then
                echo "Extracting PackageVersions.props from archive: $sourceBuiltArchive"
                tempDir=$(mktemp -d)
                tar -xzf "$sourceBuiltArchive" -C "$tempDir" PackageVersions.props || true
                if [ -f "$tempDir/PackageVersions.props" ]; then
                    packageVersionsPath="$tempDir/PackageVersions.props"
                    echo "Extracted PackageVersions.props to: $packageVersionsPath"
                fi
            fi
        fi
    fi

    if [ ! -f "$packageVersionsPath" ]; then
        echo "ERROR: Cannot find PackageVersions.props. Debugging info:"
        echo "  Attempted custom PVP path: ${custom_packages_dir}/PackageVersions.props"
        echo "  Attempted previously-source-built path: ${packagesPreviouslySourceBuiltDir}PackageVersions.props"
        echo "  Attempted archive path: $packagesArchiveDir"
        exit 1
    fi

    # Read SDK version from global.json
    local SDK_VERSION=""
    local CLI_ROOT=""

    if [ -f "$scriptroot/.dotnet/dotnet" ]; then
        SDK_VERSION=$("$scriptroot/.dotnet/dotnet" --version 2>/dev/null || echo "")
        CLI_ROOT="$scriptroot/.dotnet"
        echo "Using SDK from '$CLI_ROOT', version '$SDK_VERSION'"
    else
        echo "ERROR: .dotnet SDK not found at $scriptroot/.dotnet"
        exit 1
    fi

    # Export SDK information
    export SDK_VERSION="$SDK_VERSION"
    export CLI_ROOT="$CLI_ROOT"
    export _InitializeDotNetCli="$CLI_ROOT"
    export DOTNET_INSTALL_DIR="$CLI_ROOT"

    # Extract bootstrap versions and set environment variables for SDK resolver

    # 1. Microsoft.DotNet.Arcade.Sdk
    local arcadeSdkLine=$(grep -m 1 'MicrosoftDotNetArcadeSdkVersion' "$packageVersionsPath" || echo "")
    local arcadeSdkPattern="<MicrosoftDotNetArcadeSdkVersion>(.*)</MicrosoftDotNetArcadeSdkVersion>"
    if [[ $arcadeSdkLine =~ $arcadeSdkPattern ]]; then
        export ARCADE_BOOTSTRAP_VERSION=${BASH_REMATCH[1]}
        export SOURCE_BUILT_SDK_ID_ARCADE=Microsoft.DotNet.Arcade.Sdk
        export SOURCE_BUILT_SDK_VERSION_ARCADE=$ARCADE_BOOTSTRAP_VERSION
        export SOURCE_BUILT_SDK_DIR_ARCADE=${NUGET_PACKAGES}BootstrapPackages/microsoft.dotnet.arcade.sdk/$ARCADE_BOOTSTRAP_VERSION
        echo "Found Arcade SDK version: $ARCADE_BOOTSTRAP_VERSION"
    fi

    # 2. Microsoft.Build.NoTargets
    local notargetsSdkLine=$(grep -m 1 'Microsoft.Build.NoTargets' "$scriptroot/global.json" || echo "")
    local notargetsSdkPattern="\"Microsoft\.Build\.NoTargets\" *: *\"(.*)\""
    if [[ $notargetsSdkLine =~ $notargetsSdkPattern ]]; then
        export NOTARGETS_BOOTSTRAP_VERSION=${BASH_REMATCH[1]}
        export SOURCE_BUILT_SDK_ID_NOTARGETS=Microsoft.Build.NoTargets
        export SOURCE_BUILT_SDK_VERSION_NOTARGETS=$NOTARGETS_BOOTSTRAP_VERSION
        export SOURCE_BUILT_SDK_DIR_NOTARGETS=${NUGET_PACKAGES}BootstrapPackages/microsoft.build.notargets/$NOTARGETS_BOOTSTRAP_VERSION
        echo "Found NoTargets SDK version: $NOTARGETS_BOOTSTRAP_VERSION"
    fi

    # 3. Microsoft.Build.Traversal
    local traversalSdkLine=$(grep -m 1 'Microsoft.Build.Traversal' "$scriptroot/global.json" || echo "")
    local traversalSdkPattern="\"Microsoft\.Build\.Traversal\" *: *\"(.*)\""
    if [[ $traversalSdkLine =~ $traversalSdkPattern ]]; then
        export TRAVERSAL_BOOTSTRAP_VERSION=${BASH_REMATCH[1]}
        export SOURCE_BUILT_SDK_ID_TRAVERSAL=Microsoft.Build.Traversal
        export SOURCE_BUILT_SDK_VERSION_TRAVERSAL=$TRAVERSAL_BOOTSTRAP_VERSION
        export SOURCE_BUILT_SDK_DIR_TRAVERSAL=${NUGET_PACKAGES}BootstrapPackages/microsoft.build.traversal/$TRAVERSAL_BOOTSTRAP_VERSION
        echo "Found Traversal SDK version: $TRAVERSAL_BOOTSTRAP_VERSION"
    fi

    echo "SDK resolver setup complete. Bootstrap versions: Arcade $ARCADE_BOOTSTRAP_VERSION, NoTargets $NOTARGETS_BOOTSTRAP_VERSION, Traversal $TRAVERSAL_BOOTSTRAP_VERSION"

    # Clean up temporary directory if created
    if [[ -n "$tempDir" && -d "$tempDir" ]]; then
        rm -rf "$tempDir"
    fi
}

function setup_msbuild_sdk_resolver() {
    local scriptroot="$1"

    echo "Setting up MSBuild SDK resolver..."

    # Initialize build tool if not already done
    if [ -z "${_InitializeBuildTool:-}" ]; then
        # Source eng/common/tools.sh to get InitializeBuildTool
        if [ -f "$scriptroot/eng/common/tools.sh" ]; then
            source "$scriptroot/eng/common/tools.sh"
        else
            echo "ERROR: eng/common/tools.sh not found"
            exit 1
        fi

        InitializeBuildTool
    fi

    local dotnetPath="$_InitializeBuildTool"

    if [ ! -x "$dotnetPath" ]; then
        echo "ERROR: dotnet not found at $dotnetPath"
        exit 1
    fi

    # Build MSBuild SDK resolver
    "$dotnetPath" build-server shutdown --msbuild
    "$dotnetPath" build "$scriptroot/eng/init-source-only.proj" --verbosity minimal
    "$dotnetPath" build-server shutdown --msbuild

    # Set up MSBuild additional SDK resolvers folder
    export MSBUILDADDITIONALSDKRESOLVERSFOLDER="$scriptroot/artifacts/toolset/VSSdkResolvers/"

    echo "MSBuild SDK resolver setup complete"
}
