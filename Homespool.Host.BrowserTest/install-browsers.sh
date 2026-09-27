#!/usr/bin/env bash
# Installs the browser builds this project's Microsoft.Playwright version drives - Chromium, and
# WebKit as the stand-in for Safari. Until it has run, the browser tests skip themselves.
#
# Through the Node driver the package ships in the build output, rather than the playwright.ps1 its
# documentation suggests, so that PowerShell is not a dependency here. Builds first, because the
# driver only exists once the project has been built.
#
# Extra arguments go to the installer: --with-deps also installs the system libraries the browsers
# need, which a fresh Linux machine (CI) does not have and a Mac never needs.
#
# Usage: ./install-browsers.sh [--with-deps]

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$script_dir/Homespool.Host.BrowserTest.csproj"

dotnet build "$project" --nologo -v quiet

output="$script_dir/bin/Debug/net10.0/.playwright"
node="$(find "$output/node" -type f -name node -perm -u+x | head -n 1)"

if [ -z "$node" ]; then
    echo "No Playwright driver under $output - did the build succeed?" >&2
    exit 1
fi

"$node" "$output/package/cli.js" install "$@" chromium webkit
