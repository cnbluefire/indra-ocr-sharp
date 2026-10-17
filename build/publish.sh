#!/usr/bin/env bash
# Publish the two hosts as native AOT binaries for one RID, zip them, and smoke them.
# See docs/290-aot.md and docs/340-nuget-packages.md.  This is build/publish.ps1 for Linux and
# macOS: the two scripts do the same steps in the same order.
#
# Usage:
#   build/publish.sh --rid linux-x64 [--configuration Release] [--output temp/publish]
#                    [--skip-smoke] [--dry-run]
#
# The RID has to be the machine's: Native AOT compiles with the platform's own linker, so it does
# not cross-compile.  The release workflow gives each of the four RIDs its own runner (win-x64 on
# windows-latest, win-arm64 on windows-11-arm, linux-x64 on ubuntu-24.04, linux-arm64 on
# ubuntu-24.04-arm) and calls this script once on each.
#
# Layer 1 of the three release gates (docs/340-nuget-packages.md): INDRA_OCR_FAKE_AOT republishes
# with `-p:PublishAot=false -p:IndraOcrFakeAot=true -p:PublishSingleFile=true` for machines that have no
# platform toolchain.  The stand-in is not AOT (the JIT still compiles what the trimmer keeps), so
# the artifacts are named ..._notaot and the zip name carries it too; pack/push and the release
# refuse the suffix.
#
# What runs, in order:
#   1. dotnet publish of src/Indra.Ocr.Cli into temp/publish/<rid>/cli and of src/Indra.Ocr.Viewer
#      into temp/publish/<rid>/viewer -- one folder each, because publish owns its output folder
#      (the single-file bundler empties it) and because a trimmed publish would otherwise write
#      indra-ocr.dll twice, once as the CLI's app and once as the library the viewer links;
#   2. the zip temp/publish/indra-ocr-sharp_<rid>[_notaot].zip: both folders merged, minus their
#      symbol files (*.pdb, *.dbg), plus LICENSE and NOTICE, at the zip's root -- for native AOT
#      that is two executables and the native libraries they share, which is what a release carries;
#   3. build/smoke-linux.sh against temp/publish/<rid>: the CLI on the committed samples, then the
#      viewer over HTTP.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid=""
configuration="Release"
output="temp/publish"
skip_smoke=0
dry_run=0

while [ $# -gt 0 ]; do
    case "$1" in
        --rid) rid="$2"; shift 2 ;;
        --configuration) configuration="$2"; shift 2 ;;
        --output) output="$2"; shift 2 ;;
        --skip-smoke) skip_smoke=1; shift ;;
        --dry-run) dry_run=1; shift ;;
        *) echo "[publish] unknown argument: $1" >&2; exit 2 ;;
    esac
done

die() { echo "[publish] FAIL $1" >&2; exit 1; }

case "$rid" in
    win-x64|win-arm64|linux-x64|linux-arm64) ;;
    "") die "no RID; pass --rid (win-x64, win-arm64, linux-x64, linux-arm64)" ;;
    *) die "unknown RID '$rid'; the release has win-x64, win-arm64, linux-x64, linux-arm64" ;;
esac

fake="${INDRA_OCR_FAKE_AOT:-}"
if [ -n "$fake" ]; then suffix="_notaot"; else suffix=""; fi
case "$output" in /*) ;; *) output="$root/$output" ;; esac
dir="$output/$rid"
stage="$output/$rid-stage"
zip="$output/indra-ocr-sharp_$rid$suffix.zip"
logs="$root/temp/logs"

step() { echo; echo "[publish] ==== $1"; }

run_tool() {
    echo "[publish]   $*"
    if [ "$dry_run" -eq 1 ]; then
        echo "[publish]   (dry run: not executed)"
        return 0
    fi
    "$@" || die "$1 exited with $? (see the logs in $logs)"
}

mkdir -p "$logs"

step "the target ($rid, $configuration)"
if [ -n "$fake" ]; then
    echo "[publish] INDRA_OCR_FAKE_AOT is set: publishing the trimmed stand-in, NOT AOT."
    echo "[publish] The artifacts are named ...$suffix and the release refuses them."
else
    echo "[publish] Native AOT (PublishAot is set by the two host projects)."
fi
echo "[publish] folder: $dir/cli and $dir/viewer"
echo "[publish] zip:    $zip"

step "an empty folder"
if [ -d "$dir" ]; then
    echo "[publish] removing $dir"
    if [ "$dry_run" -eq 0 ]; then rm -rf "$dir"; fi
fi
if [ -e "$zip" ] || [ -d "$stage" ]; then
    echo "[publish] removing the previous zip/stage"
    if [ "$dry_run" -eq 0 ]; then rm -rf "$zip" "$stage"; fi
fi
if [ "$dry_run" -eq 0 ]; then mkdir -p "$dir"; fi

# PublishAot is a property the projects set, so the AOT run needs no switches; the stand-in turns it
# back off and asks the two hosts for trimming (-p:IndraOcrFakeAot, which they turn into
# PublishTrimmed: as a global property it would reach the libraries' netstandard2.0 target, where
# trimming does not exist).  Single file as well, so the stand-in's two folders merge the way native
# AOT's do -- one executable per host plus the native libraries they share, no second indra-ocr.dll.
extra=()
if [ -n "$fake" ]; then extra=(-p:PublishAot=false -p:IndraOcrFakeAot=true -p:PublishSingleFile=true); fi

# One publish folder per host, in the order that goes into the zip.
for pair in Indra.Ocr.Cli:cli Indra.Ocr.Viewer:viewer; do
    host="${pair%%:*}"
    folder="${pair##*:}"
    step "dotnet publish $host ($rid) -> $folder"
    run_tool dotnet publish "$root/src/$host/$host.csproj" \
        -c "$configuration" \
        -r "$rid" \
        -o "$dir/$folder" \
        --self-contained true \
        -v:n "-bl:$logs/publish-$rid-$host.binlog" \
        ${extra[@]+"${extra[@]}"}
done

step "the zip"
if [ "$dry_run" -eq 1 ]; then
    echo "[publish]   (dry run: the folders are not there to zip)"
else
    command -v zip >/dev/null 2>&1 || die "zip is not installed (it is what keeps the executable bit; apt-get install zip)"
    mkdir -p "$stage"
    kept=0
    for folder in cli viewer; do
        [ -d "$dir/$folder" ] || die "nothing was published into $dir/$folder"
        while IFS= read -r -d '' file; do
            case "$file" in *.pdb|*.dbg) continue ;; esac
            relative="${file#"$dir/$folder"/}"
            mkdir -p "$stage/$(dirname "$relative")"
            cp "$file" "$stage/$relative"
            kept=$((kept + 1))
        done < <(find "$dir/$folder" -type f -print0)
    done
    cp "$root/LICENSE" "$root/NOTICE" "$stage/"
    echo "[publish] $kept published file(s) (symbol files dropped) + LICENSE + NOTICE"
    ( cd "$stage" && zip -r -q "$zip" . )
    rm -rf "$stage"
    echo "[publish] $zip ($(wc -c < "$zip") bytes)"
fi

if [ "$skip_smoke" -eq 1 ]; then
    step "the smoke (skipped: --skip-smoke)"
elif [ "$dry_run" -eq 1 ]; then
    step "the smoke (skipped: dry run)"
else
    step "the smoke (build/smoke-linux.sh)"
    "$root/build/smoke-linux.sh" --dir "$dir"
fi

step "the artifacts"
if [ -d "$output" ]; then
    for file in "$output"/*; do
        [ -f "$file" ] || continue
        printf '[publish]   %12d  %s\n' "$(wc -c < "$file")" "$(basename "$file")"
    done
fi
echo
if [ -n "$fake" ]; then
    echo "[publish] OK: $rid published (stand-in, not AOT)"
else
    echo "[publish] OK: $rid published as native AOT"
fi
