@echo off
setlocal

set "REPO_ROOT=%~dp0.."
pushd "%REPO_ROOT%" || exit /b 1

dotnet restore backend\NexoBar.slnx || goto :error
dotnet format backend\NexoBar.slnx --verify-no-changes --no-restore || goto :error
dotnet build backend\NexoBar.slnx --no-restore || goto :error
dotnet test backend\NexoBar.slnx --no-build --no-restore || goto :error

pushd frontend || goto :error
call npm ci || goto :frontend_error
call npm run typecheck || goto :frontend_error
call npm run lint || goto :frontend_error
call npm run format:check || goto :frontend_error
call npm run build || goto :frontend_error
popd

popd
exit /b 0

:frontend_error
set "VERIFY_EXIT_CODE=%ERRORLEVEL%"
popd
goto :finish_error

:error
set "VERIFY_EXIT_CODE=%ERRORLEVEL%"

:finish_error
popd
exit /b %VERIFY_EXIT_CODE%
