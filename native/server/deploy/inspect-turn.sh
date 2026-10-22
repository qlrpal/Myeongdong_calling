#!/usr/bin/env bash
# Read-only systemd service accounting. No config, firewall or capture changes.
set -euo pipefail
seconds=${1:-10}
if [[ ! "$seconds" =~ ^([1-9]|[1-5][0-9]|60)$ ]]; then
  printf '%s\n' 'Usage: inspect-turn.sh [1..60 seconds]' >&2
  exit 2
fi
before=$(systemctl show coturn -p CPUUsageNSec --value)
start=$(date +%s%N)
sleep "$seconds"
after=$(systemctl show coturn -p CPUUsageNSec --value)
end=$(date +%s%N)
memory=$(systemctl show coturn -p MemoryCurrent --value)
state=$(systemctl show coturn -p ActiveState --value)
cpu=null
if [[ "$before" =~ ^[0-9]+$ && "$after" =~ ^[0-9]+$ && "$start" =~ ^[0-9]+$ && "$end" =~ ^[0-9]+$ ]]; then
  cpu=$(awk -v a="$before" -v b="$after" -v s="$start" -v e="$end" 'BEGIN {if (b>=a && e>s) printf "%.6f",(b-a)/(e-s); else printf "null"}')
fi
[[ "$memory" =~ ^[0-9]+$ ]] || memory=null
[[ "$state" =~ ^[a-z-]+$ ]] || state=unknown
printf '{"service":"coturn","state":"%s","requestedSeconds":%s,"cpuCores":%s,"memoryBytes":%s,"note":"Aggregate service accounting; traffic is not attributed. Null means unavailable/reset. Not a packet-rate load qualification."}\n' "$state" "$seconds" "$cpu" "$memory"
