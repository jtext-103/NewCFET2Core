#!/usr/bin/env bash
set -euo pipefail
export PATH="$HOME/.dotnet:$CI_PROJECT_DIR/.dotnet:$PATH"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^10\.0\.401 '; then
  curl --fail --silent --show-error --location https://dot.net/v1/dotnet-install.sh -o "$CI_PROJECT_DIR/.ci/dotnet-install.sh"
  bash "$CI_PROJECT_DIR/.ci/dotnet-install.sh" --version 10.0.401 --install-dir "$CI_PROJECT_DIR/.dotnet" --no-path
  export PATH="$CI_PROJECT_DIR/.dotnet:$PATH"
fi
export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
dotnet --version
