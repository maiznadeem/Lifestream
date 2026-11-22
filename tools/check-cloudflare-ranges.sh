#!/usr/bin/env bash
# Compares Cloudflare's published IPv4 ranges with the ingress allow-list in infra/main.bicep (param ingressAllowedRanges).
# The container app accepts only those ranges, so a range Cloudflare adds would answer 403 to its visitors without warning.
# Changes nothing. Exit 0: the sets are equal. Exit 1: they differ (the added and removed ranges are printed). Exit 2: the
# fetch failed or returned nothing usable, or the Bicep list could not be parsed.
# Usage: tools/check-cloudflare-ranges.sh [path/to/main.bicep]   (CLOUDFLARE_IPS_URL overrides the source, for tests)
set -euo pipefail

bicep=${1:-"$(dirname "$0")/../infra/main.bicep"}
url=${CLOUDFLARE_IPS_URL:-https://www.cloudflare.com/ips-v4}
cidr='^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$'

[ -f "$bicep" ] || { echo "ERROR: $bicep not found"; exit 2; }

# The lines between `param ingressAllowedRanges array = [` and the closing `]`; every one must be a quoted range.
block=$(awk '/^param ingressAllowedRanges array = \[/ { on = 1; next } on && /^\]/ { found = 1; exit } on { print } END { if (!found) exit 1 }' "$bicep" | tr -d '\r') \
  || { echo "ERROR: param ingressAllowedRanges array = [ ... ] not found in $bicep"; exit 2; }
ours=$(printf '%s\n' "$block" | sed -E "s/^[[:space:]]*'([^']*)'[[:space:]]*$/\1/" | sed '/^$/d')
[ -n "$ours" ] || { echo "ERROR: ingressAllowedRanges in $bicep is empty"; exit 2; }
if bad=$(printf '%s\n' "$ours" | grep -Ev "$cidr"); then echo "ERROR: cannot parse these lines of ingressAllowedRanges in $bicep:"; printf '%s\n' "$bad" | sed 's/^/  /'; exit 2; fi

published=$(curl -fsSL --max-time 20 --retry 2 "$url" | tr -d '\r' | sed '/^[[:space:]]*$/d') || { echo "ERROR: fetching $url failed"; exit 2; }
[ -n "$published" ] || { echo "ERROR: $url returned no ranges"; exit 2; }
if bad=$(printf '%s\n' "$published" | grep -Ev "$cidr"); then echo "ERROR: $url returned something that is not a list of IPv4 ranges:"; printf '%s\n' "$bad" | head -5 | sed 's/^/  /'; exit 2; fi

ours=$(printf '%s\n' "$ours" | sort -u); published=$(printf '%s\n' "$published" | sort -u)
added=$(comm -13 <(printf '%s\n' "$ours") <(printf '%s\n' "$published"))
removed=$(comm -23 <(printf '%s\n' "$ours") <(printf '%s\n' "$published"))
echo "Cloudflare publishes $(printf '%s\n' "$published" | wc -l | tr -d ' ') IPv4 ranges ($url); ingressAllowedRanges lists $(printf '%s\n' "$ours" | wc -l | tr -d ' ')."
if [ -z "$added" ] && [ -z "$removed" ]; then echo "OK: the lists match."; exit 0; fi
if [ -n "$added" ]; then echo "ADDED by Cloudflare, missing from ingressAllowedRanges (their visitors get 403):"; printf '%s\n' "$added" | sed 's/^/  /'; fi
if [ -n "$removed" ]; then echo "REMOVED by Cloudflare, still in ingressAllowedRanges:"; printf '%s\n' "$removed" | sed 's/^/  /'; fi
echo "DRIFT: update ingressAllowedRanges in infra/main.bicep and apply the template (infra/README.md)."
exit 1
