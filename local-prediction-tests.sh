#!/usr/bin/env bash
# Local emulation of .github/workflows/prediction-tests.yml "Run scenarios" step.
# Usage: MODE=server COUNT=3 ./local-prediction-tests.sh [profile ...]
#   MODE  = server | host        (default server)
#   COUNT = total player count   (default 3)
# Profiles default to the same three the workflow runs.
set -u

BIN="${BIN:-build/StandaloneWindows64/PurrDictionTests.exe}"
MODE="${MODE:-server}"
COUNT="${COUNT:-3}"
CONNECT_DELAY="${CONNECT_DELAY:-1}"
PROFILE_TIMEOUT_SECONDS="${PROFILE_TIMEOUT_SECONDS:-420}"

if [ ! -f "$BIN" ]; then
    echo "ERROR: player binary not found at $BIN (run LocalTestBuild.BuildWindows first)"
    exit 1
fi

if [ "$MODE" = "host" ]; then
    EXTERNAL_CLIENTS=$((COUNT - 1))
else
    EXTERNAL_CLIENTS="$COUNT"
fi

FAIL=0

run_profile() {
    PROFILE="$1"
    PORT="$2"
    shift 2
    LATENCY_ARGS=("$@")

    RESULTS_DIR="$PWD/test-results/$MODE/$PROFILE"
    LOGS_DIR="$PWD/test-logs/$MODE/$PROFILE"
    rm -rf "$RESULTS_DIR" "$LOGS_DIR"
    mkdir -p "$RESULTS_DIR" "$LOGS_DIR"

    echo "=== [$MODE/$PROFILE] launching $MODE process on port $PORT ==="
    "$BIN" -batchmode -nographics \
        -role "$MODE" -count "$COUNT" -port "$PORT" \
        -connectTimeout 180 \
        "${LATENCY_ARGS[@]}" \
        -results "$RESULTS_DIR/$MODE.json" \
        -logFile "$LOGS_DIR/$MODE.log" &
    SERVER_PID=$!

    sleep "$CONNECT_DELAY"

    READY=0
    for _ in $(seq 1 60); do
        if grep -q '\[PredictionTests\].*local connection ready' "$LOGS_DIR/$MODE.log" 2>/dev/null; then
            READY=1
            break
        fi
        if ! kill -0 "$SERVER_PID" 2>/dev/null; then
            break
        fi
        sleep 1
    done

    if [ "$READY" -ne 1 ]; then
        echo "ERROR: $MODE process not ready ($PROFILE)"
        tail -n 60 "$LOGS_DIR/$MODE.log" 2>/dev/null || true
        kill "$SERVER_PID" 2>/dev/null || true
        FAIL=1
        return
    fi

    CLIENT_PIDS=()
    for i in $(seq 1 "$EXTERNAL_CLIENTS"); do
        echo "=== [$MODE/$PROFILE] launching client $i ==="
        "$BIN" -batchmode -nographics \
            -role client -count "$COUNT" -port "$PORT" \
            -connectTimeout 180 \
            "${LATENCY_ARGS[@]}" \
            -results "$RESULTS_DIR/client-$i.json" \
            -logFile "$LOGS_DIR/client-$i.log" &
        CLIENT_PIDS+=("$!")
        [ "$i" -lt "$EXTERNAL_CLIENTS" ] && sleep 1
    done

    ALL_PIDS=("$SERVER_PID" "${CLIENT_PIDS[@]}")
    (
        sleep "$PROFILE_TIMEOUT_SECONDS"
        echo "ERROR: profile '$PROFILE' exceeded ${PROFILE_TIMEOUT_SECONDS}s; killing processes"
        for pid in "${ALL_PIDS[@]}"; do
            kill "$pid" 2>/dev/null || true
        done
    ) &
    WATCHDOG_PID=$!

    for pid in "${CLIENT_PIDS[@]}"; do
        if wait "$pid"; then
            echo "[$MODE/$PROFILE] client PID $pid exited 0"
        else
            echo "ERROR: [$MODE/$PROFILE] client PID $pid exited nonzero"
            FAIL=1
        fi
    done

    if wait "$SERVER_PID"; then
        echo "[$MODE/$PROFILE] $MODE process exited 0"
    else
        echo "ERROR: [$MODE/$PROFILE] $MODE process exited nonzero"
        FAIL=1
    fi

    kill "$WATCHDOG_PID" 2>/dev/null || true
    wait "$WATCHDOG_PID" 2>/dev/null || true

    python - "$RESULTS_DIR" <<'EOF'
import json, sys, glob, os
fail = False
for f in sorted(glob.glob(os.path.join(sys.argv[1], "*.json"))):
    with open(f) as fh:
        data = json.load(fh)
    bad = [d for d in data if not d.get("result", {}).get("success", False)]
    tag = os.path.basename(f)
    if bad:
        fail = True
        print(f"FAILED {tag}: {len(bad)}/{len(data)} scenarios")
        for d in bad:
            print(f"  - {d.get('name')}: {d.get('result', {}).get('message', '')[:400]}")
    else:
        print(f"ok {tag}: {len(data)}/{len(data)} scenarios passed")
sys.exit(1 if fail else 0)
EOF
    if [ $? -ne 0 ]; then
        FAIL=1
    fi
}

if [ $# -gt 0 ]; then
    for p in "$@"; do
        case "$p" in
            default)     run_profile default 37001 -latencyMin 40 -latencyMax 80 ;;
            no-latency)  run_profile no-latency 37002 -latencyMax 0 ;;
            high-jitter) run_profile high-jitter 37003 -latencyMin 120 -latencyMax 250 ;;
            loss)        run_profile loss 37004 -latencyMin 40 -latencyMax 80 -packetLoss 5 ;;
            heavy-loss)  run_profile heavy-loss 37005 -latencyMin 80 -latencyMax 160 -packetLoss 15 ;;
            ownership-swap) run_profile ownership-swap 37011 -latencyMin 40 -latencyMax 80 -packetLoss 5 -ownershipSwapScenarioOnly ;;
            *) echo "unknown profile $p"; FAIL=1 ;;
        esac
    done
else
    run_profile default 37001 -latencyMin 40 -latencyMax 80
    run_profile no-latency 37002 -latencyMax 0
    run_profile high-jitter 37003 -latencyMin 120 -latencyMax 250
fi

exit $FAIL
