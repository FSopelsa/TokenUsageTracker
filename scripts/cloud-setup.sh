#!/bin/bash
# Setup script for the Claude cloud environment. Paste it into the
# environment's "Setup script" field (it is kept here only for reference and
# review; the environment does not read it from the repository).
#
# Installs only the .NET 10 SDK, from Ubuntu's own signed archive:
# no credentials, no curl | bash, no third-party package feeds.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
# The setup script cannot see the environment's variables, so the opt-outs
# are set here as well; otherwise the SDK's first run sends telemetry.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  apt-get update -qq
  apt-get install -y -qq --no-install-recommends dotnet-sdk-10.0
fi
dotnet --version
