#!/usr/bin/env bash
# Smoke the published hosts of one RID on Linux, with nothing but a shell and curl: the CLI on the
# committed samples, then the viewer over plain HTTP requests.  See docs/340-nuget-packages.md.
#
# Usage: build/smoke-linux.sh [--dir temp/publish/linux-x64] [--model-dir <dir>] [--port 8799]
#
# The checks are deliberately coarse.  Both hosts are the port end to end (native AOT in a release,
# the trimmed stand-in with INDRA_OCR_FAKE_AOT), so this answers "does the published binary start,
# read a PDF and an image, write its output, and serve the viewer's API" -- the byte-for-byte
# comparison against the reference lives in tools/scripts/test/cli_parity.py and the test suite, and
# needs the Python reference environment, which a release machine does not have.
#
# The model folder is resources/models when it is there (a developer's clone); otherwise the
# argument is left out and both hosts resolve the models themselves -- they download them from the
# Hub, which is what a release user does and what the CI runners exercise (TLS, JSON and the
# download path included).
#
# Versions are compared by prefix only: the CLI prints `indra-ocr <version>` and the viewer reports
# the assembly's version, and the exact value belongs to src/Directory.Build.props, which is read
# here rather than repeated.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dir=""
model_dir=""
port=8799

while [ $# -gt 0 ]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --model-dir) model_dir="$2"; shift 2 ;;
        --port) port="$2"; shift 2 ;;
        *) echo "smoke-linux: unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$dir" ]; then
    case "$(uname -m)" in
        aarch64|arm64) dir="$root/temp/publish/linux-arm64" ;;
        *) dir="$root/temp/publish/linux-x64" ;;
    esac
fi
case "$dir" in /*) ;; *) dir="$root/$dir" ;; esac
if [ -z "$model_dir" ] && [ -d "$root/resources/models" ]; then
    model_dir="$root/resources/models"
fi

logs="$root/temp/logs"
work="$root/temp/smoke"
mkdir -p "$logs" "$work"

# build/publish.* publishes one folder per host (cli/, viewer/); a RID folder that holds both hosts
# directly is accepted too, which is what a hand-run `dotnet publish ... -o <rid>` of the two
# projects produces.
find_host() {  # find_host NAME -> the path of the host, or where it was looked for
    for candidate in "$dir/$1" "$dir/cli/$1" "$dir/viewer/$1"; do
        if [ -f "$candidate" ]; then
            echo "$candidate"
            return 0
        fi
    done
    echo "$dir/$1"
}

cli="$(find_host indra-ocr)"
viewer="$(find_host indra-ocr-viewer)"
png="$root/build/smoke/latin.png"
pdf="$root/build/smoke/latin.pdf"
version="$(sed -n 's/^[[:space:]]*<Version>\([^<]*\)<\/Version>.*/\1/p' "$root/src/Directory.Build.props" | head -1)"

checks=0
failures=()

section() { echo; echo "== $1"; }

check() {  # check WHAT OK [DETAIL]
    checks=$((checks + 1))
    if [ "$2" = "1" ]; then
        echo "  ok   $1"
    else
        echo "  FAIL $1${3:+: $3}"
        failures+=("$1")
    fi
}

tail_show() {  # tail_show PATH [LINES]
    [ -f "$1" ] || return 0
    echo "  --- $1 (last ${2:-12} lines)"
    tail -n "${2:-12}" "$1" | sed 's/^/  | /'
}

# The CLI with its output merged (stderr carries --timings, so it has to be shown on failure).
cli_out=""
cli_exit=0
run_cli() {
    cli_out="$("$cli" "$@" 2>&1)"
    cli_exit=$?
}

# One HTTP request: the body lands in HTTP_BODY, the status in HTTP_STATUS.  Email/comma-joined
# arguments are avoided on purpose; curl is the one HTTP client every runner has.
http_body=""
http_status=0
http() {  # http METHOD PATH [BODY-FILE] [CONTENT-TYPE]
    local method="$1" path="$2" body="${3:-}" ctype="${4:-}"
    local -a args=(-s -X "$method" -o "$work/body" -w '%{http_code}' --max-time 900)
    [ -n "$ctype" ] && args+=(-H "Content-Type: $ctype")
    [ -n "$body" ] && args+=(--data-binary "@$body")
    http_status="$(curl "${args[@]}" "http://127.0.0.1:$port$path" || echo 000)"
    http_body="$(cat "$work/body" 2>/dev/null || true)"
}

model_args=()
[ -n "$model_dir" ] && model_args=(--model-dir "$model_dir")

echo "smoke-linux: the published hosts in $dir"
echo "  version:  $version (from src/Directory.Build.props)"
echo "  models:   ${model_dir:-none: the hosts resolve them themselves}"
echo "  viewer:   http://127.0.0.1:$port"

section "the published folder"
check "indra-ocr is there" "$([ -f "$cli" ] && echo 1 || echo 0)" "$cli"
check "indra-ocr-viewer is there" "$([ -f "$viewer" ] && echo 1 || echo 0)" "$viewer"
if [ ! -f "$cli" ] || [ ! -f "$viewer" ]; then
    echo "smoke-linux: FAILED (the hosts are not published)"
    exit 1
fi
check "the CLI is executable" "$([ -x "$cli" ] && echo 1 || echo 0)"

# The zip is what a release carries, so its contents are checked rather than the publish folder's:
# the folder keeps its symbol files on disk, the zip is where they are dropped.  zip/unzip are what
# build/publish.sh needs anyway; without unzip the checks are skipped rather than failed.
output_dir="$(dirname "$dir")"
zips=()
for candidate in "$output_dir/indra-ocr-sharp_"*.zip; do
    [ -e "$candidate" ] || continue
    zips+=("$candidate")
done
if [ "${#zips[@]}" -eq 0 ]; then
    echo "  (no zip beside the publish folder: the zip checks are skipped)"
elif ! command -v unzip >/dev/null 2>&1; then
    echo "  (unzip is not installed: the zip checks are skipped)"
else
    archive="$(ls -t "${zips[@]}" | head -1)"
    entries="$(unzip -Z1 "$archive")"
    check "the zip ($(basename "$archive")) has no symbol files" \
          "$(printf '%s\n' "$entries" | grep -qE '\.(pdb|dbg)$' && echo 0 || echo 1)"
    check "the zip holds both hosts at its root" \
          "$(printf '%s\n' "$entries" | grep -qx 'indra-ocr' && printf '%s\n' "$entries" | grep -qx 'indra-ocr-viewer' && echo 1 || echo 0)"
    check "the zip carries the licence files" \
          "$(printf '%s\n' "$entries" | grep -qx 'LICENSE' && printf '%s\n' "$entries" | grep -qx 'NOTICE' && echo 1 || echo 0)"
    check "the zip keeps the hosts executable" \
          "$(unzip -Z "$archive" | grep -qE '^-rwxr-xr-x.*[[:space:]]indra-ocr$' && echo 1 || echo 0)"
fi

section "the command line"
run_cli --version
check "indra-ocr --version is the port" "$(printf '%s' "$cli_out" | grep -qE '^[[:space:]]*indra-ocr ' && printf 1 || printf 0)"
case "$cli_out" in *"$version"*) ok=1 ;; *) ok=0 ;; esac
check "the version is the props' one" "$ok" "$(printf '%s' "$cli_out" | head -2 | tr '\n' ' ')"

run_cli --help
check "indra-ocr --help prints its usage" "$([ "$cli_exit" = 0 ] && printf '%s' "$cli_out" | grep -q 'usage: indra-ocr' && echo 1 || echo 0)"

run_cli "$png" --format text "${model_args[@]+"${model_args[@]}"}"
check "the PNG reads back its invoice number" "$([ "$cli_exit" = 0 ] && printf '%s' "$cli_out" | grep -q '12345' && echo 1 || echo 0)" "exit $cli_exit"

run_cli "$pdf" --format text "${model_args[@]+"${model_args[@]}"}"
check "the PDF's text layer is read" "$([ "$cli_exit" = 0 ] && printf '%s' "$cli_out" | grep -qF 'Invoice number: 12345' && echo 1 || echo 0)" "exit $cli_exit"

run_cli "$pdf" --mode ocr --format json "${model_args[@]+"${model_args[@]}"}"
check "the PDF OCRs when asked (json lines)" "$([ "$cli_exit" = 0 ] && printf '%s' "$cli_out" | grep -q '12345' && printf '%s' "$cli_out" | grep -q '"page"' && echo 1 || echo 0)" "exit $cli_exit"

section "the written output"
out="$work/out"
rm -rf "$out"
run_cli "$png" --out "$out" "${model_args[@]+"${model_args[@]}"}"
written="$out/latin.md"
check "indra-ocr --out writes one markdown file per input" \
      "$([ "$cli_exit" = 0 ] && [ -f "$written" ] && grep -q '12345' "$written" && echo 1 || echo 0)" "exit $cli_exit"

run_cli "$work/there-is-no-such-file.png"
check "a missing input is an error" "$([ "$cli_exit" != 0 ] && echo 1 || echo 0)" "exit $cli_exit"

section "the viewer"
# "n" is the page index the browser's page list sends, and it is 0-based: the reference reads the
# page with doc[n], pypdfium2's __getitem__.
viewer_out="$logs/smoke-viewer.out"
viewer_err="$logs/smoke-viewer.err"
rm -f "$viewer_out" "$viewer_err"
"$viewer" --port "$port" --threads 2 "${model_args[@]+"${model_args[@]}"}" >"$viewer_out" 2>"$viewer_err" &
viewer_pid=$!
trap 'kill "$viewer_pid" 2>/dev/null || true' EXIT

# The models are resolved (and downloaded) before the server listens, so the wait is generous: a
# cold CI runner downloads them here.
info=""
deadline=$((SECONDS + 300))
while [ "$SECONDS" -lt "$deadline" ]; do
    if ! kill -0 "$viewer_pid" 2>/dev/null; then break; fi
    http GET /info
    if [ "$http_status" = 200 ]; then info="$http_body"; break; fi
    sleep 0.5
done

check "the viewer answers GET /info" "$([ -n "$info" ] && echo 1 || echo 0)" "log: $viewer_err"
if [ -z "$info" ]; then
    tail_show "$viewer_out"
    tail_show "$viewer_err"
else
    check "tesseract is off" "$(printf '%s' "$info" | grep -q '"tesseract":[[:space:]]*false' && echo 1 || echo 0)" "$info"
    check "the version is the assembly's" "$(printf '%s' "$info" | sed -n 's/.*"version":[[:space:]]*"\([^"]*\)".*/\1/p' | grep -qx "$version" && echo 1 || echo 0)" "$info"
    check "the defaults come back" "$(printf '%s' "$info" | grep -q '"max_pages":[[:space:]]*20' && echo 1 || echo 0)" "$info"

    http GET /
    check "GET / serves the page" "$([ "$http_status" = 200 ] && printf '%s' "$http_body" | grep -q 'indra-ocr' && echo 1 || echo 0)" "status $http_status"

    http POST /upload "$png" application/octet-stream
    upload_id="$(printf '%s' "$http_body" | sed -n 's/.*"id":[[:space:]]*"\([^"]*\)".*/\1/p')"
    check "POST /upload takes the PNG" "$([ "$http_status" = 200 ] && printf '%s' "$http_body" | grep -q '"pages":[[:space:]]*1' && echo 1 || echo 0)" "status $http_status: $http_body"

    if [ -n "$upload_id" ]; then
        printf '{"id":"%s","n":0}' "$upload_id" >"$work/page.json"
        http POST /page "$work/page.json" application/json
        check "POST /page reads the image" "$([ "$http_status" = 200 ] && printf '%s' "$http_body" | grep -q '12345' && echo 1 || echo 0)" "status $http_status"
        check "the answer carries the preview, the boxes and the timings" \
              "$(printf '%s' "$http_body" | grep -q '"img":[[:space:]]*"data:image/jpeg;base64,' \
                 && printf '%s' "$http_body" | grep -q '"lines":[[:space:]]*\[{' \
                 && printf '%s' "$http_body" | grep -q '"page":[[:space:]]*1' \
                 && printf '%s' "$http_body" | grep -q '"total_ms":[[:space:]]*[1-9]' \
                 && echo 1 || echo 0)" "status $http_status"
    else
        check "POST /upload answers with an id" 0 "$http_body"
    fi

    http POST /upload "$pdf" application/octet-stream
    upload_id="$(printf '%s' "$http_body" | sed -n 's/.*"id":[[:space:]]*"\([^"]*\)".*/\1/p')"
    check "POST /upload takes the PDF" "$([ "$http_status" = 200 ] && printf '%s' "$http_body" | grep -q '"pages":[[:space:]]*1' && echo 1 || echo 0)" "status $http_status: $http_body"
    if [ -n "$upload_id" ]; then
        printf '{"id":"%s","n":0}' "$upload_id" >"$work/page.json"
        http POST /page "$work/page.json" application/json
        check "POST /page reads the PDF's text layer" \
              "$([ "$http_status" = 200 ] && printf '%s' "$http_body" | grep -qF 'Invoice number: 12345' && echo 1 || echo 0)" "status $http_status"
    else
        check "POST /upload answers with an id" 0 "$http_body"
    fi

    printf '{"n": 1}' >"$work/bad.json"
    http POST /page "$work/bad.json" application/json
    check "a page request without an id is a 400" \
          "$([ "$http_status" = 400 ] && printf '%s' "$http_body" | grep -q '"error"' && echo 1 || echo 0)" "status $http_status: $http_body"
fi

kill "$viewer_pid" 2>/dev/null || true
wait "$viewer_pid" 2>/dev/null || true
echo "  stopped the viewer (pid $viewer_pid)"

echo
if [ "${#failures[@]}" -gt 0 ]; then
    echo "smoke-linux: FAILED (${#failures[@]} of $checks checks)"
    for failure in "${failures[@]}"; do echo "  - $failure"; done
    exit 1
fi

echo "smoke-linux: OK ($checks checks)"
exit 0
