#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_DIR="$REPO_ROOT/.github/agents"

if [[ ! -d "$SOURCE_DIR" ]]; then
  echo "Source directory not found: $SOURCE_DIR" >&2
  exit 1
fi

TARGETS=(
  "$HOME/.config/Code/User/prompts"
  "$HOME/.config/Code - Insiders/User/prompts"
)

for target in "${TARGETS[@]}"; do
  mkdir -p "$target"

  for file in "$SOURCE_DIR"/*.agent.md; do
    name="$(basename "$file")"
    ln -sfn "$file" "$target/$name"
  done

  echo "Installed Atlas agents to: $target"
done

echo
cat <<'EOF'
Done. Reload VS Code window, then try:
- @Prometheus plan a feature in this repo
- @Atlas execute the plan in plans/<task>-plan.md
EOF
