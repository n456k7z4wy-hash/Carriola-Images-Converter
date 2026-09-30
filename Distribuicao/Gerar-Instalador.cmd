@echo off
setlocal
title Gerar instalador - Carriola Images Converter
rem A politica abaixo vale somente para este processo de compilacao local.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Gerar-Instalador.ps1"
set "carriola_build_result=%errorlevel%"
echo.
if not "%carriola_build_result%"=="0" echo A geracao falhou. Leia a mensagem acima; nao distribua um instalador antigo.
pause
exit /b %carriola_build_result%
