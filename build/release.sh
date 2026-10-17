#!/usr/bin/env bash
# Create the GitHub release for one version: the three packages, their symbol packages, the four
# per-RID host zips, and the notes.  See docs/340-nuget-packages.md.
#
# Usage:
#   build/release.sh [--version 0.3.2-alpha.1] [--packages temp/packages] [--zips temp/publish]
#                    [--dry-run]
#
# This is the last step of the release workflow and the only one that talks to GitHub; `gh` takes
# the token from GH_TOKEN (the workflow passes github.token) and the repository from the checkout.
# The version is not an argument by default: it comes from src/Directory.Build.props, the one place
# it is written down (Version and PackageFlavor composed the way the packages compose them, so the
# tag and the asset names carry the package version, 0.3.2-alpha.1).
#
# Layer 2 of the three release gates (docs/340-nuget-packages.md): a stand-in artifact -- the
# _notaot.zip build/publish.* writes when INDRA_OCR_FAKE_AOT is set -- must never reach a release,
# both by the environment variable and by the file names, whichever is the one that got through.
#
# Re-running is safe: an existing release gets the assets replaced (`gh release upload --clobber`),
# which is what a run that died after the push needs.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
packages="temp/packages"
zips="temp/publish"
dry_run=0
rids=(win-x64 win-arm64 linux-x64 linux-arm64)
# The package ids, which are not the assembly names (see docs/340-nuget-packages.md).
ids=(Bluefire.Indra.Ocr.Sharp.Core Bluefire.Indra.Ocr.Sharp.Pdf Bluefire.Indra.Ocr.Sharp)

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version="$2"; shift 2 ;;
        --packages) packages="$2"; shift 2 ;;
        --zips) zips="$2"; shift 2 ;;
        --dry-run) dry_run=1; shift ;;
        *) echo "[release] unknown argument: $1" >&2; exit 2 ;;
    esac
done

die() { echo "[release] FAIL $1" >&2; exit 1; }
step() { echo; echo "[release] ==== $1"; }

if [ -n "${INDRA_OCR_FAKE_AOT:-}" ]; then
    die "INDRA_OCR_FAKE_AOT is set: the trimmed stand-in must not be released"
fi

if [ -z "$version" ]; then
    # Version + PackageFlavor, exactly as build/pack.* and src/Directory.Build.props compose them:
    # the package version is what the assets are named after and what the tag carries.
    base="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>.*/\1/p' \
        "$root/src/Directory.Build.props" | head -1)"
    flavor="$(sed -n 's/^[[:space:]]*<PackageFlavor>\([^<]*\)<\/PackageFlavor>.*/\1/p' \
        "$root/src/Directory.Build.props" | head -1)"
    [ -n "$base" ] || die "no version: src/Directory.Build.props has no <Version>, and none was passed"
    version="$base"
    if [ -n "$flavor" ]; then version="$base-$flavor"; fi
fi
case "$packages" in /*) ;; *) packages="$root/$packages" ;; esac
case "$zips" in /*) ;; *) zips="$root/$zips" ;; esac

case "$version" in
    *-*) prerelease=1 ;;   # 0.3.2-alpha.1: a prerelease on GitHub too
    *) prerelease=0 ;;
esac

step "the assets of v$version"
assets=()
for id in "${ids[@]}"; do
    nupkg="$packages/$id.$version.nupkg"
    snupkg="$packages/$id.$version.snupkg"
    [ -f "$nupkg" ] || die "$nupkg is missing (build/pack.* writes the packages)"
    [ -f "$snupkg" ] || die "$snupkg is missing (dotnet pack writes the symbols beside the package)"
    assets+=("$nupkg" "$snupkg")
    echo "[release]   $(basename "$nupkg")"
    echo "[release]   $(basename "$snupkg")"
done
for rid in "${rids[@]}"; do
    zip="$zips/indra-ocr-sharp_$rid.zip"
    fake_zip="$zips/indra-ocr-sharp_${rid}_notaot.zip"
    if [ -f "$fake_zip" ]; then
        die "$(basename "$fake_zip") is a stand-in; publish the real AOT zips"
    fi
    [ -f "$zip" ] || die "$zip is missing (build/publish.* writes one zip per RID)"
    assets+=("$zip")
    echo "[release]   $(basename "$zip")"
done
echo "[release]   ${#assets[@]} assets"

step "the notes"
# The commits are the notes; --generate-notes asks GitHub for its own (the pull requests) and gh
# prepends what --notes-file holds, so the release reads as the log and then GitHub's summary.
notes="$(mktemp)"
previous="$(git describe --tags --abbrev=0 2>/dev/null || true)"
if [ -n "$previous" ]; then
    echo "[release] commits since $previous"
    range="$previous..HEAD"
    start_tag_args=(--notes-start-tag "$previous")
else
    echo "[release] no previous tag: every commit is in the notes"
    range="HEAD"
    start_tag_args=()
fi
{
    echo "The commits in this release:"
    echo
    git log --oneline "$range"
} >"$notes"
echo "[release] note lines: $(wc -l <"$notes")"

if [ "$prerelease" -eq 1 ]; then
    echo "[release] v$version is a prerelease (it carries a suffix)"
else
    echo "[release] v$version is a normal release"
fi

create_args=(--title "v$version" --generate-notes --notes-file "$notes")
if [ "$prerelease" -eq 1 ]; then create_args+=(--prerelease); fi
if [ "${#start_tag_args[@]}" -gt 0 ]; then create_args+=("${start_tag_args[@]}"); fi

if [ "$dry_run" -eq 1 ]; then
    step "the release (dry run: nothing is created)"
    echo "[release]   gh release view v$version  # existing? then upload --clobber"
    echo "[release]   gh release create v$version <${#assets[@]} assets> ${create_args[*]}"
    rm -f "$notes"
    echo
    echo "[release] OK: v$version is ready (dry run)"
    exit 0
fi

command -v gh >/dev/null 2>&1 || die "gh is not installed (it is what creates the release)"

step "the release"
if gh release view "v$version" >/dev/null 2>&1; then
    echo "[release] v$version exists: replacing its assets"
    gh release upload "v$version" "${assets[@]}" --clobber
else
    gh release create "v$version" "${assets[@]}" "${create_args[@]}"
fi
rm -f "$notes"

step "what GitHub holds now"
gh release view "v$version" --json tagName,isPrerelease,assets \
    --template '{{.tagName}} (prerelease: {{.isPrerelease}}){{"\n"}}{{range .assets}}  {{.name}}{{"\n"}}{{end}}'

echo
echo "[release] OK: v$version is on GitHub"
