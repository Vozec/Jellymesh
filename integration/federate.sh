#!/usr/bin/env bash
# Build a bidirectional A<->B<->C federation (no A<->C — we test introductions on it).
# For each link, the "owner" issues a share key, the other side adds the owner as a peer.
set -eu
cd "$(dirname "$0")"
source tokens.env

issue_share() {
  local on_token="$1" on_base="$2" label="$3"
  curl -fsS -X POST "$on_base/Federation/Shares" \
    -H "X-Emby-Token: $on_token" -H "Content-Type: application/json" \
    -d "{\"Label\":\"$label\"}" | grep -oP '"ApiKey":"\K[^"]+'
}

set_public_url() {
  local on_token="$1" on_base="$2" url="$3"
  local cfg
  cfg=$(curl -fsS "$on_base/Plugins/9f3c2a8e-6b1d-4f7a-b3c5-1e2d9a8b7c6e/Configuration" -H "X-Emby-Token: $on_token")
  cfg=$(echo "$cfg" | python3 -c "import sys,json; c=json.load(sys.stdin); c['PublicBaseUrl']='$url'; print(json.dumps(c))")
  curl -fsS -X POST "$on_base/Plugins/9f3c2a8e-6b1d-4f7a-b3c5-1e2d9a8b7c6e/Configuration" \
    -H "X-Emby-Token: $on_token" -H "Content-Type: application/json" --data-binary "$cfg" >/dev/null
}

add_peer() {
  local on_token="$1" on_base="$2" peer_name="$3" peer_url="$4" peer_api_key="$5" fed_share_key="$6"
  local cfg
  cfg=$(curl -fsS "$on_base/Plugins/9f3c2a8e-6b1d-4f7a-b3c5-1e2d9a8b7c6e/Configuration" -H "X-Emby-Token: $on_token")
  cfg=$(echo "$cfg" | python3 -c "
import sys, json, uuid
c = json.load(sys.stdin)
c.setdefault('RemoteServers', []).append({
    'Id': str(uuid.uuid4()),
    'Name': '$peer_name',
    'BaseUrl': '$peer_url',
    'ApiKey': '$peer_api_key',
    'FederationShareKey': '$fed_share_key',
    'RemoteUserId': '',
    'LocalUserIdForSync': '',
    'BasicAuthUser': '',
    'BasicAuthPass': '',
    'Enabled': True,
    'AllowedLibraryIds': []
})
print(json.dumps(c))
")
  curl -fsS -X POST "$on_base/Plugins/9f3c2a8e-6b1d-4f7a-b3c5-1e2d9a8b7c6e/Configuration" \
    -H "X-Emby-Token: $on_token" -H "Content-Type: application/json" --data-binary "$cfg" >/dev/null
}

# 1. Set PublicBaseUrl on each so push invalidation can fire.
echo "Setting PublicBaseUrl on each instance"
set_public_url "$TOKEN_A" "$BASE_A" "$HOST_A"
set_public_url "$TOKEN_B" "$BASE_B" "$HOST_B"
set_public_url "$TOKEN_C" "$BASE_C" "$HOST_C"

# 2. Issue share keys.
echo "Issuing share keys"
A_for_B=$(issue_share "$TOKEN_A" "$BASE_A" "A->B")
A_for_C=$(issue_share "$TOKEN_A" "$BASE_A" "A->C")
B_for_A=$(issue_share "$TOKEN_B" "$BASE_B" "B->A")
B_for_C=$(issue_share "$TOKEN_B" "$BASE_B" "B->C")
C_for_B=$(issue_share "$TOKEN_C" "$BASE_C" "C->B")

# 3. Wire RemoteServers. A<->B and B<->C only. A<->C left for introduction tests.
echo "Wiring peers (A<->B, B<->C)"
add_peer "$TOKEN_A" "$BASE_A" "B" "$HOST_B" "$TOKEN_B" "$B_for_A"
add_peer "$TOKEN_B" "$BASE_B" "A" "$HOST_A" "$TOKEN_A" "$A_for_B"
add_peer "$TOKEN_B" "$BASE_B" "C" "$HOST_C" "$TOKEN_C" "$C_for_B"
add_peer "$TOKEN_C" "$BASE_C" "B" "$HOST_B" "$TOKEN_B" "$B_for_C"

# Persist the A_for_C key for the introduction-test phase (A pre-issues it, B asks A to mint
# for C via /Introduce in the actual scenario, but we also keep this manual one for fallback).
echo "A_for_C=$A_for_C" >> tokens.env

echo "Done. Federation: A<->B and B<->C wired. Introductions can be tested for A<->C."
