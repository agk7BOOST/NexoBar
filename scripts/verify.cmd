@echo off
setlocal

set "RUN_E2E=0"
if "%~1"=="" goto :arguments_valid
if /I not "%~1"=="--e2e" goto :usage
if not "%~2"=="" goto :usage
set "RUN_E2E=1"

:arguments_valid
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
call npm run test:run || goto :frontend_error
call npm run build || goto :frontend_error
popd

if "%RUN_E2E%"=="1" (
    pushd frontend || goto :error
    call npm run test:e2e || goto :frontend_error
    popd
)

popd
exit /b 0

:usage
echo Usage: scripts\verify.cmd [--e2e] 1>&2
exit /b 2

:frontend_error
set "VERIFY_EXIT_CODE=%ERRORLEVEL%"
popd
goto :finish_error

:error
set "VERIFY_EXIT_CODE=%ERRORLEVEL%"

:finish_error
popd
exit /b %VERIFY_EXIT_CODE%
