#!/usr/bin/env bash
# Complete the Jellyfin first-run wizard via API on each of A/B/C,
# then create a Movies library pointed at /media, and emit AdminToken
# + UserId per instance.
set -eu

declare -A PORTS=([a]=8096 [b]=8097 [c]=8098)
JF_AUTH='MediaBrowser Client="setup", Device="cli", DeviceId="setup", Version="1"'
OUT=/home/vozec/Desktop/dev/JellyfinFederation/integration/tokens.env
: > "$OUT"

for who in a b c; do
  port=${PORTS[$who]}
  base="http://localhost:$port"
  echo "[$who] setup at $base"

  # 1. Initial language config.
  curl -fsS -X POST "$base/Startup/Configuration" \
    -H "Content-Type: application/json" \
    -H "Authorization: $JF_AUTH" \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null

  # 2. Set user. GET first seeds the default 'root' user; POST renames + sets password.
  curl -fsS "$base/Startup/User" -H "Authorization: $JF_AUTH" >/dev/null
  curl -fsS -X POST "$base/Startup/User" \
    -H "Content-Type: application/json" \
    -H "Authorization: $JF_AUTH" \
    -d '{"Name":"admin","Password":"adminpw"}' >/dev/null

  # 3. Remote-access default.
  curl -fsS -X POST "$base/Startup/RemoteAccess" \
    -H "Content-Type: application/json" \
    -H "Authorization: $JF_AUTH" \
    -d '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null || true

  # 4. Complete wizard.
  curl -fsS -X POST "$base/Startup/Complete" -H "Authorization: $JF_AUTH" >/dev/null

  # 5. Authenticate to get an access token.
  AUTH_RESP=$(curl -fsS -X POST "$base/Users/AuthenticateByName" \
    -H "Content-Type: application/json" \
    -H "Authorization: $JF_AUTH" \
    -d '{"Username":"admin","Pw":"adminpw"}')
  TOKEN=$(echo "$AUTH_RESP" | grep -oP '"AccessToken":"\K[^"]+')
  USER_ID=$(echo "$AUTH_RESP" | grep -oP '"Id":"\K[^"]+' | head -1)
  echo "[$who] token=${TOKEN:0:10}… user=$USER_ID"

  # 6. Add /media as a Movies library (different name per instance so they share TMDB id but differ in some files later).
  curl -fsS -X POST "$base/Library/VirtualFolders?name=Movies&collectionType=movies&paths=/media&refreshLibrary=true" \
    -H "Content-Type: application/json" \
    -H "X-Emby-Token: $TOKEN" >/dev/null

  echo "TOKEN_${who^^}=$TOKEN" >> "$OUT"
  echo "USERID_${who^^}=$USER_ID" >> "$OUT"
  echo "BASE_${who^^}=$base" >> "$OUT"
  echo "HOST_${who^^}=http://jellyfin-${who}:8096" >> "$OUT"
done

echo
echo "Wrote $OUT:"
cat "$OUT"
