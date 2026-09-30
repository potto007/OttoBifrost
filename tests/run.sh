#!/usr/bin/env bash
# Build the mod without packaging or deploying, then run the off-runtime unit
# tests against the built DLL. Exit status is the test result.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
configuration="${MOD_CONFIGURATION:-Release}"

"$dotnet" build "$repo/OttoBifrost.csproj" -c "$configuration" -p:SkipPackage=true -p:CopyOutputDLLPath= -nologo -v:m
"$dotnet" test "$here/OttoBifrost.Tests/OttoBifrost.Tests.csproj" -p:ModConfiguration="$configuration" -nologo -v:m "$@"
