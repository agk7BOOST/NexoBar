#!/usr/bin/env bash
set -euo pipefail

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
npm run build
