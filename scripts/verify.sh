#!/usr/bin/env bash
set -euo pipefail

run_e2e=false
case "$#" in
  0) ;;
  1)
    if [[ "$1" != "--e2e" ]]; then
      echo "Usage: ./scripts/verify.sh [--e2e]" >&2
      exit 2
    fi
    run_e2e=true
    ;;
  *)
    echo "Usage: ./scripts/verify.sh [--e2e]" >&2
    exit 2
    ;;
esac

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dotnet restore backend/NexoBar.slnx
dotnet format backend/NexoBar.slnx --verify-no-changes --no-restore
dotnet build backend/NexoBar.slnx --no-restore
dotnet test backend/NexoBar.slnx --no-build --no-restore

cd frontend
npm ci
npm run typecheck
npm run lint
npm run format:check
npm run test:run
npm run build

if [[ "$run_e2e" == true ]]; then
  npm run test:e2e
fi
