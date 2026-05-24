#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

# CachyOS / Arch ships only .NET 10 SDK; tests target net8 (Jellyfin compat).
# If a user-local .NET 8 runtime was installed (~/.dotnet/shared/Microsoft.AspNetCore.App/8.0.*)
# point dotnet at it so testhost can boot.
if [ -d "$HOME/.dotnet/shared/Microsoft.AspNetCore.App" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi

dotnet test tests/Jellyfin.Plugin.Federation.Tests/Jellyfin.Plugin.Federation.Tests.csproj --nologo "$@"
