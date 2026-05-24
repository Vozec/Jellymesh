#!/usr/bin/env bash
# Extract every ```mermaid``` block from docs/*.md and render to docs/diagrams/<basename>-<idx>.png.
set -euo pipefail
cd "$(dirname "$0")"

mkdir -p diagrams

for md in architecture.md protocol.md sync-flow.md introductions.md; do
    base="${md%.md}"
    awk -v base="$base" '
        /^```mermaid$/ { capture=1; idx++; out=sprintf("diagrams/%s-%d.mmd", base, idx); next }
        /^```$/ && capture { capture=0; print "wrote", out > "/dev/stderr"; next }
        capture { print > out }
    ' "$md"
done

# mmdc uses Puppeteer → headless Chromium. Some hosts ship neither; install
# chrome-headless-shell into the user cache if missing.
if ! find "$HOME/.cache/puppeteer/chrome-headless-shell" -type f -name chrome-headless-shell 2>/dev/null | head -1 | grep -q .; then
    npx -y puppeteer browsers install chrome-headless-shell
fi
SHELL_BIN=$(find "$HOME/.cache/puppeteer/chrome-headless-shell" -type f -name chrome-headless-shell 2>/dev/null | head -1)

PUPPETEER_CFG=$(mktemp)
cat > "$PUPPETEER_CFG" <<JSON
{
    "args": ["--no-sandbox", "--disable-setuid-sandbox"],
    "executablePath": "$SHELL_BIN"
}
JSON

for mmd in diagrams/*.mmd; do
    svg="${mmd%.mmd}.svg"
    npx -y -p @mermaid-js/mermaid-cli mmdc -i "$mmd" -o "$svg" -p "$PUPPETEER_CFG" -b transparent -t dark 2>&1 | tail -3
done

rm "$PUPPETEER_CFG"
echo "Done. SVGs in docs/diagrams/"
