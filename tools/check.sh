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
log_dir=${CHECK_LOG_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/openbudget-check.XXXXXX")}
mkdir -p "$log_dir"
unit_only=false
[[ ${1:-} == --unit-only ]] && unit_only=true

start=$(date +%s)
echo "build ..."
# One build for both test projects (they share the app project); the test runs below use --no-build.
if ! dotnet build tests/OpenBudget.E2E.Tests -v quiet -nologo > "$log_dir/build-e2e.log" 2>&1 \
    || ! dotnet build tests/OpenBudget.Tests -v quiet -nologo > "$log_dir/build-unit.log" 2>&1; then
    tail -n 30 "$log_dir"/build-*.log
    echo "FAILED: build ($log_dir)"
    exit 1
fi
echo "build ok ($(( $(date +%s) - start ))s)"

pids=()
names=()
run() { # name, args...
    local name=$1; shift
    ( dotnet test --no-build -nologo -tl:off "$@" > "$log_dir/$name.log" 2>&1 ) &
    pids+=($!)
    names+=("$name")
}

run unit tests/OpenBudget.Tests
if ! $unit_only; then
    e2e_args=(tests/OpenBudget.E2E.Tests)
    [[ -n ${E2E_WORKERS:-} ]] && e2e_args+=(-- "NUnit.NumberOfTestWorkers=$E2E_WORKERS")
    run e2e "${e2e_args[@]}"
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
        summary=$(tr "\r" "\n" < "$log_dir/$name.log" | grep -oE "(Passed|Failed)! +- Failed: +[0-9]+, Passed: +[0-9]+" | tail -n 1 || true)
        if (( code == 0 )); then
            echo "$name ok ($(( $(date +%s) - start ))s) $summary"
        else
            echo "FAILED: $name ($(( $(date +%s) - start ))s) $summary"
            grep -E "^\s+Failed |Error Message" -A3 "$log_dir/$name.log" | head -n 40 || tail -n 40 "$log_dir/$name.log"
            status=1
            stop_all
            break
        fi
    done
done

echo "logs: $log_dir"
exit $status
