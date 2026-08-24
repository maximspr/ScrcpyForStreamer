@echo off
rem Сборка ScrcpyForStreamer на Windows.
rem Архивы лежат в deps\ под официальными именами; имя ресурса внутри exe
rem задаётся вторым параметром /resource и остаётся scrcpy.zip / gnirehtet.zip.
rem VER можно переопределить переменной окружения (это делает CI из тега).
rem /codepage:65001 обязателен: PhoneScreen.cs в UTF-8 без BOM, без флага
rem русские строки на машине с другой ANSI-кодировкой превратятся в мусор.

if "%VER%"=="" set VER=1_0_0
set OUT=dist\ScrcpyForStreamer-win64-v%VER%.exe

set SCRCPY=deps\scrcpy-win64-v4.1.zip
set GNIREHTET=deps\gnirehtet-rust-win64-v2.5.1.zip

if not exist "%SCRCPY%"    (echo. & echo НЕ НАЙДЕН: %SCRCPY%    & call :hold & exit /b 1)
if not exist "%GNIREHTET%" (echo. & echo НЕ НАЙДЕН: %GNIREHTET% & call :hold & exit /b 1)

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (echo. & echo НЕ НАЙДЕН КОМПИЛЯТОР csc.exe & call :hold & exit /b 1)

if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /win32icon:app.ico /out:"%OUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /resource:"%SCRCPY%",scrcpy.zip /resource:"%GNIREHTET%",gnirehtet.zip PhoneScreen.cs

if errorlevel 1 (echo. & echo СБОРКА НЕ УДАЛАСЬ & call :hold & exit /b 1)
echo. & echo Готово: %OUT%
call :hold
exit /b 0

:hold
rem В CI паузу не ставим — нажимать некому, job зависнет.
if "%CI%"=="" pause
exit /b 0
