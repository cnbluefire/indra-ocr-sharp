#!/usr/bin/env bash
# Pack the three NuGet packages, check what each one holds and run the smoke consumer against them.
# See docs/340-nuget-packages.md.  This is build/pack.ps1 for Linux and macOS: the two scripts do the
# same steps in the same order, and neither one knows the version -- src/Directory.Build.props does.
#
# Usage:
#   build/pack.sh [--configuration Release] [--output temp/packages] [--model-dir <dir>]
#                 [--skip-smoke] [--skip-net462] [--dry-run]
#
# What runs, in order:
#   1. dotnet pack src/Indra.Ocr.slnx -c Release -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg
#      (the solution, so the two hosts are compiled against the libraries being packed too);
#   2. tools/scripts/test/check_nupkg.py, which checks the six files entry by entry;
#   3. tools/nuget-smoke, a consumer that references the packages -- never the projects -- and walks
#      their public surface on net10.0 (and on net462 where .NET Framework exists, i.e. on Windows).
#
# The logs go to temp/logs/ (pack.binlog), which the workflows upload when a step fails.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"
output="temp/packages"
model_dir=""
skip_smoke=0
skip_net462=0
dry_run=0

while [ $# -gt 0 ]; do
    case "$1" in
        --configuration) configuration="$2"; shift 2 ;;
        --output) output="$2"; shift 2 ;;
        --model-dir) model_dir="$2"; shift 2 ;;
        --skip-smoke) skip_smoke=1; shift ;;
        --skip-net462) skip_net462=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        *) echo "[pack] unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$output" in
    /*) packages="$output" ;;
    *) packages="$root/$output" ;;
esac
logs="$root/temp/logs"
smoke="$root/tools/nuget-smoke"
assets="$root/build/smoke"

step() { echo; echo "[pack] ==== $1"; }
die() { echo "[pack] FAIL $1" >&2; exit 1; }

# Runs one external command, logging it first.  --dry-run only logs.
run_tool() {
    echo "[pack]   $*"
    if [ "$dry_run" -eq 1 ]; then
        echo "[pack]   (dry run: not executed)"
        return 0
    fi
    "$@" || die "$1 exited with $?"
}

find_python() {
    if [ -n "${INDRA_OCR_PYTHON:-}" ]; then echo "$INDRA_OCR_PYTHON"; return; fi
    # The venv of tools/scripts/setup-pyenv.ps1, whichever side of the Windows line this shell is.
    for candidate in "$root/tools/pyenv/bin/python" "$root/tools/pyenv/Scripts/python.exe"; do
        if [ -f "$candidate" ]; then echo "$candidate"; return; fi
    done
    for name in python3 python; do
        if command -v "$name" >/dev/null 2>&1; then command -v "$name"; return; fi
    done
    die "no Python found; set INDRA_OCR_PYTHON"
}

mkdir -p "$logs"

step "the version (src/Directory.Build.props)"
# The one place the version lives; the .nuspecs substitute it, so nothing here types it either.
version="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>.*/\1/p' "$root/src/Directory.Build.props" | head -1)"
flavor="$(sed -n 's/^[[:space:]]*<PackageFlavor>\([^<]*\)<\/PackageFlavor>.*/\1/p' "$root/src/Directory.Build.props" | head -1)"
[ -n "$version" ] || die "src/Directory.Build.props has no <Version>"
if [ -n "$flavor" ]; then package_version="$version-$flavor"; else package_version="$version"; fi
echo "[pack] PackageVersion $package_version ($version + $flavor)"

step "the package folder ($packages)"
if [ -d "$packages" ]; then
    echo "[pack] removing the folder and its $(find "$packages" -maxdepth 1 -type f | wc -l) file(s), so nothing stale survives"
    if [ "$dry_run" -eq 0 ]; then
        rm -rf "$packages"
    fi
fi
if [ "$dry_run" -eq 0 ]; then
    mkdir -p "$packages"
fi

step "dotnet pack ($configuration)"
binlog="$logs/pack.binlog"
echo "[pack] binlog: $binlog"
run_tool dotnet pack "$root/src/Indra.Ocr.slnx" \
    -c "$configuration" \
    -o "$packages" \
    -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg \
    -v:n "-bl:$binlog"

step "the packages, entry by entry"
python="$(find_python)"
run_tool "$python" "$root/tools/scripts/test/check_nupkg.py" "$packages" "$package_version"

if [ "$skip_smoke" -eq 1 ]; then
    step "the smoke consumer (skipped: --skip-smoke)"
else
    step "the smoke consumer (tools/nuget-smoke)"
    if [ -z "$model_dir" ] && [ -d "$root/resources/models" ]; then
        model_dir="$root/resources/models"
    fi
    if [ -n "$model_dir" ]; then
        echo "[pack] model folder: $model_dir"
    else
        echo "[pack] no model folder: the smoke resolves one from INDRA_OCR_MODEL_DIR or the Hub"
    fi

    # -p:RestoreAdditionalProjectSources, in case --output moved the packages away from the feed
    # tools/nuget-smoke/nuget.config points at.
    set -- run --project "$smoke" -c "$configuration" \
        "-p:SmokePackageVersion=$package_version" \
        "-p:RestoreAdditionalProjectSources=$packages"

    echo "[pack] net10.0:"
    if [ -n "$model_dir" ]; then
        run_tool dotnet "$@" -f net10.0 -- "$assets" "$model_dir"
    else
        run_tool dotnet "$@" -f net10.0 -- "$assets"
    fi

    if [ "${OS:-}" = "Windows_NT" ] && [ "$skip_net462" -eq 0 ]; then
        echo "[pack] net462 (the netstandard2.0 assets):"
        if [ -n "$model_dir" ]; then
            run_tool dotnet "$@" -f net462 -- "$assets" "$model_dir"
        else
            run_tool dotnet "$@" -f net462 -- "$assets"
        fi
    else
        echo "[pack] net462: skipped (only .NET Framework runs it; the Windows job does)"
    fi
fi

step "the artifacts"
if [ -d "$packages" ]; then
    for file in "$packages"/*; do
        [ -e "$file" ] || continue
        printf '[pack]   %10d  %s\n' "$(wc -c < "$file")" "$(basename "$file")"
    done
fi
echo
if [ "$skip_smoke" -eq 1 ]; then
    echo "[pack] OK: $package_version packed and checked"
else
    echo "[pack] OK: $package_version packed, checked and smoked"
fi
