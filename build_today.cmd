@echo off
rem 오늘 날짜로 버전 표기를 갱신하고 패치 프로그램을 빌드합니다.
rem 1) Xbox 360 타이틀 화면의 "한글패치 vYYMMDD"를 오늘 날짜로 다시 그리고 디스크 차분을 다시 만듭니다.
rem 2) build.ps1을 실행합니다. PC 타이틀 표기와 프로그램 버전은 build.ps1이 오늘 날짜로 갱신합니다.
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo [1/2] Xbox 360 타이틀 버전 표기 갱신
python -B "%~dp0tools\refresh_xbox_title_version.py"
if errorlevel 1 goto fail

echo [2/2] 패치 프로그램 빌드
rem PowerShell 7(pwsh)이 있으면 사용하고, 없으면 모듈 경로를 기본값으로 되돌린 Windows PowerShell을 사용합니다.
where pwsh >nul 2>nul
if errorlevel 1 (
  set "PSModulePath="
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
) else (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
)
if errorlevel 1 goto fail

echo 완료: bin 폴더의 SO4KoreanPatcher.Xdelta-v*.zip 을 확인하세요.
exit /b 0

:fail
echo 실패했습니다. 위의 오류 메시지를 확인하세요.
exit /b 1
