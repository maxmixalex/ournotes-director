#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")"

case "$(uname -s)" in
    Linux) host_rid=linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) host_rid=win-x64 ;;
    *) printf '%s\n' 'Build on Windows or Linux, or use the GitHub Actions matrix.' >&2; exit 2 ;;
esac
rid=$host_rid
publish=true
for argument in "$@"; do
    case "$argument" in
        win-x64|linux-x64) rid=$argument ;;
        --no-publish) publish=false ;;
        *) printf 'Usage: %s [win-x64|linux-x64] [--no-publish]\n' "$0" >&2; exit 2 ;;
    esac
done
if "$publish" && [[ "$rid" != "$host_rid" ]]; then
    printf 'Native AOT requires the target OS. Build %s there, or use the GitHub Actions matrix.\n' "$rid" >&2
    exit 2
fi

export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export DOTNET_NOLOGO=true
dotnet build OurNotes.Director.sln -c Release --nologo
dotnet run --project tests/OurNotes.Director.Tests -c Release --no-build --no-restore
if "$publish"; then
    dotnet publish src/OurNotes.Director.Cli -c Release -r "$rid" -o "artifacts/$rid" --nologo
    printf 'Native executable: artifacts/%s/advkit\n' "$rid"
fi
