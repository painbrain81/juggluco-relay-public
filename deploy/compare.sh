#!/bin/bash
# Compares the relay's data with the official Juggluco server's while both run side by side.
#   sudo bash compare.sh [juggluco-home] [juggluco-user]
# juggluco-home is the folder holding the official server's ./juggluco binary and its jugglucodata
# (default /home/juggluco); juggluco-user is the account it runs as (default juggluco).
set -uo pipefail

OLD_HOME=${1:-/home/juggluco}
OLD_USER=${2:-juggluco}
OLD=$OLD_HOME/jugglucodata
NEW=/var/lib/juggluco-relay/files
# The relay's stream.tsv (StreamExport.Path); the official server's is produced now with juggluco -B
NEW_TSV=${NEW_TSV:-/var/lib/juggluco-relay/stream.tsv}
TMPDIR_OLD=$(mktemp -d)
trap 'rm -rf "$TMPDIR_OLD"' EXIT
chown "$OLD_USER" "$TMPDIR_OLD"
OLD_TSV="$TMPDIR_OLD/stream.tsv"
(cd "$OLD_HOME" && sudo -u "$OLD_USER" ./juggluco -B "$OLD_TSV" >/dev/null 2>&1) ||
    echo "(export from the official server failed: the stream.tsv comparison will be skipped)"

echo "== polls.dat (stream) per sensor, compared on the common part"
for d in "$NEW"/sensors/*/; do
    s=$(basename "$d")
    n="$NEW/sensors/$s/polls.dat"; o="$OLD/sensors/$s/polls.dat"
    [ -f "$n" ] || continue
    if [ ! -f "$o" ]; then echo "  $s: missing on the official server"; continue; fi
    ln=$(stat -c %s "$n"); lo=$(stat -c %s "$o")
    l=$(( ln < lo ? ln : lo ))
    if cmp -s -n "$l" "$n" "$o"; then
        echo "  $s: identical on the first $l bytes (relay $ln, official $lo)"
    else
        echo "  $s: DIFFERENT ($(cmp -n "$l" "$n" "$o" | head -1))"
    fi
done

echo
echo "== stream.tsv on the common rows (up to the last value present in both)"
if [ -s "$OLD_TSV" ] && [ -s "$NEW_TSV" ]; then
    lo=$(tail -n1 "$OLD_TSV" | cut -f3); ln=$(tail -n1 "$NEW_TSV" | cut -f3)
    lim=$(( lo < ln ? lo : ln ))
    diffs=$(diff <(awk -F'\t' -v l="$lim" 'NR==1 || $3<=l' "$OLD_TSV") \
                 <(awk -F'\t' -v l="$lim" 'NR==1 || $3<=l' "$NEW_TSV") | head -20)
    if [ -z "$diffs" ]; then
        echo "  identical up to $(date -d @"$lim" '+%F %T') ($(wc -l < "$NEW_TSV") rows from the relay)"
    else
        echo "  DIFFERENCES (first 20 diff lines, < official, > relay):"
        echo "$diffs"
    fi
else
    echo "  one of the two files is missing (is StreamExport enabled?)"
fi

echo
echo "== last value"
echo "  official: $(tail -n1 "$OLD_TSV" 2>/dev/null | cut -f4,7)"
echo "  relay:    $(tail -n1 "$NEW_TSV" 2>/dev/null | cut -f4,7)"
