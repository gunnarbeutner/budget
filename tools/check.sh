#!/usr/bin/env bash
# Pre-deploy check: builds once, then runs the unit and E2E suites in parallel. Fails fast: the first suite to fail
# stops the other one. Exit code 0 only when both pass. Logs: $CHECK_LOG_DIR (default: a temp dir, printed).
#
#   tools/check.sh              # both suites
#   tools/check.sh --unit-only  # skip E2E
#   E2E_WORKERS=4 tools/check.sh  # E2E fixture workers (default: LevelOfParallelism in the E2E AssemblyInfo)
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"
# dotnet format loads MSBuild from DOTNET_ROOT: point it at the installation of the dotnet on PATH, so an older SDK
# elsewhere (which cannot load the projects, leaving their style rules unchecked) is never used.
dotnet_bin=$(command -v dotnet)
export DOTNET_ROOT=$(cd "$(dirname "$(readlink -f "$dotnet_bin")")" && pwd)
log_dir=${CHECK_LOG_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/openbudget-check.XXXXXX")}
mkdir -p "$log_dir"
unit_only=false
[[ ${1:-} == --unit-only ]] && unit_only=true

start=$(date +%s)
echo "build ..."
# One build for both test projects (they share the app project); the test runs below use --no-build.
# The Release publish is what the Docker image runs; it compiles some Razor differently from Debug, so it runs when a
# .razor/.cshtml file differs from origin/main (committed or not), or always with CHECK_RELEASE=1.
release_publish=${CHECK_RELEASE:-}
if [[ -z $release_publish ]]; then
    release_publish=0
    if ! git rev-parse -q --verify origin/main >/dev/null \
        || { git diff --name-only origin/main; git ls-files --others --exclude-standard; } | grep -qE '\.(razor|cshtml)$'; then
        release_publish=1
    fi
fi
publish() {
    [[ $release_publish == 1 ]] || { echo "(Release publish skipped: no Razor changes)" > "$log_dir/build-release.log"; return 0; }
    dotnet publish src/OpenBudget/OpenBudget.csproj -c Release -o "$log_dir/publish" -v quiet -nologo \
        /p:UseAppHost=false /p:RunAnalyzers=false > "$log_dir/build-release.log" 2>&1
}
if ! dotnet build tests/OpenBudget.E2E.Tests -v quiet -nologo > "$log_dir/build-e2e.log" 2>&1 \
    || ! dotnet build tests/OpenBudget.Tests -v quiet -nologo > "$log_dir/build-unit.log" 2>&1 \
    || ! publish; then
    tail -n 30 "$log_dir"/build-*.log
    echo "FAILED: build ($log_dir)"
    exit 1
fi
echo "build ok ($(( $(date +%s) - start ))s)"

pids=()
names=()
run() { # name, args...
    local name=$1; shift
    # The normal-verbosity console logger writes each failed test's name, error message and stack trace to the log.
    ( dotnet test --no-build -nologo -tl:off --logger "trx;LogFileName=$name.trx" --logger "console;verbosity=normal" \
        --results-directory "$log_dir" "$@" \
        > "$log_dir/$name.log" 2>&1 ) &
    pids+=($!)
    names+=("$name")
}
trx_summary() { # "Passed!  - Failed: 0, Passed: 12" from a trx file's counters (empty when there is none)
    local counters failed passed
    counters=$(grep -oE '<Counters [^>]*>' "$1" 2>/dev/null) || return 0
    failed=$(sed -E 's/.* failed="([0-9]+)".*/\1/' <<< "$counters")
    passed=$(sed -E 's/.* passed="([0-9]+)".*/\1/' <<< "$counters")
    if (( failed == 0 )); then echo "Passed!  - Failed: $failed, Passed: $passed"
    else echo "Failed!  - Failed: $failed, Passed: $passed"; fi
}

# The formatting gate CI runs (lint job), alongside the tests.
( for target in budget.slnx tests/OpenBudget.E2E.Tests/OpenBudget.E2E.Tests.csproj; do
      dotnet format whitespace "$target" --verify-no-changes --no-restore || exit 1
      dotnet format style "$target" --verify-no-changes --no-restore || exit 1
  done > "$log_dir/format.log" 2>&1 ) &
pids+=($!)
names+=(format)
run unit tests/OpenBudget.Tests
if ! $unit_only; then
    e2e_args=(tests/OpenBudget.E2E.Tests)
    [[ -n ${E2E_WORKERS:-} ]] && e2e_args+=(-- "NUnit.NumberOfTestWorkers=$E2E_WORKERS")
    OB_E2E_TRACE_DIR="$log_dir/e2e-traces" run e2e "${e2e_args[@]}"
fi

kill_tree() { # the suite's subshell, dotnet test and its testhost
    local child
    for child in $(pgrep -P "$1" 2>/dev/null); do kill_tree "$child"; done
    kill "$1" 2>/dev/null || true
}
stop_all() { for p in "${pids[@]}"; do kill_tree "$p"; done; }
trap 'stop_all' INT TERM

status=0
remaining=${#pids[@]}
finished=()
# Poll (macOS ships bash 3.2, without `wait -n`): whichever suite finishes first is reported; a failure stops the
# other one right away.
while (( remaining > 0 && status == 0 )); do
    sleep 1
    for i in "${!pids[@]}"; do
        [[ " ${finished[*]-} " == *" $i "* ]] && continue
        kill -0 "${pids[$i]}" 2>/dev/null && continue
        finished+=("$i")
        remaining=$(( remaining - 1 ))
        set +e; wait "${pids[$i]}"; code=$?; set -e
        name=${names[$i]}
        summary=$(trx_summary "$log_dir/$name.trx")
        if (( code == 0 )); then
            echo "$name ok ($(( $(date +%s) - start ))s) $summary"
        else
            echo "FAILED: $name ($(( $(date +%s) - start ))s) $summary"
            # The trx file names the failed tests reliably; the console log is garbled by progress output.
            if [[ -f $log_dir/$name.trx ]]; then
                grep -oE 'testName="[^"]+"[^>]*outcome="Failed"' "$log_dir/$name.trx" | sed -E 's/testName="([^"]+)".*/  failed: \1/' || true
            fi
            tr "\r" "\n" < "$log_dir/$name.log" | grep -E "^ +Failed [^:]|Error Message" -A4 | head -n 60 \
                || tail -n 40 "$log_dir/$name.log"
            status=1
            stop_all
            break
        fi
    done
done

echo "logs: $log_dir"
exit $status
