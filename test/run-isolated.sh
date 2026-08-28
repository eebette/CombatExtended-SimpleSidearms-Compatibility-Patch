#!/usr/bin/env bash
# Every phase in its own process, against a freshly loaded save.
#
# The sequenced run (run-assert.sh) proves the phases work against accumulated
# state. This proves each one stands alone — which arranging cannot demonstrate
# on its own, because a phase can arrange everything its author remembered and
# still lean on something they did not.
#
# Slow by construction: one game launch per phase, ~90s each. A pre-release
# sweep, not something to run on every edit.
#
# Usage:
#   ./test/run-isolated.sh cetest1 CETEST-1-pickup
#   ./test/run-isolated.sh cetest2 CETEST-2-selection
#   ./test/run-isolated.sh cetest3 CETEST-3-combat
#   ./test/run-isolated.sh cetest4 CETEST-4-generation
set -euo pipefail

SCENARIO="${1:?scenario (cetest1..4)}"
SAVE="${2:?save name}"

REPO="$(cd "$(dirname "$0")/.." && pwd)"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$REPO/test/SaveData"

if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/CESimpleSidearmsCompat/CESimpleSidearmsCompat.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/TestStaging.csproj" -c Release
fi

rm -f "$SAVEDATA/test-results-$SCENARIO-iso-"*.json

run_one() {
    timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
        "-celoadsave=$SAVE" "-ceassert=$SCENARIO:$1" >/dev/null 2>&1 || true
}

# Phase 0 also reports how many phases the scenario has, so the sweep does not
# need to know the count in advance.
echo "== isolated sweep: $SCENARIO =="
run_one 0
FIRST="$SAVEDATA/test-results-$SCENARIO-iso-00.json"
if [[ ! -f "$FIRST" ]]; then
    echo "== phase 0 produced no results; check Player.log ==" >&2
    exit 1
fi
COUNT=$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['phaseCount'])" "$FIRST")
echo "   $COUNT phases"

for ((i = 1; i < COUNT; i++)); do
    printf '   phase %d/%d\n' "$i" "$((COUNT - 1))"
    run_one "$i"
done

# No config-poison gate here on purpose: nothing in these scenarios writes mod
# settings to disk (the Loadouts module is switched off in-memory, and the SS
# settings the phases flip are never Write()n).

exec "$(dirname "$0")/verdict.py" --merge "$SAVEDATA/test-results-$SCENARIO-iso-"*.json
