@echo off
rem Пересборка PhoneScreen.exe на Windows.
rem Рядом должны лежать: PhoneScreen.cs, app.ico, scrcpy.zip, gnirehtet.zip
rem   scrcpy.zip    - официальный scrcpy-win64-v4.1.zip
rem   gnirehtet.zip - официальный gnirehtet-rust-win64-v2.5.1.zip

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /optimize+ /win32icon:app.ico /out:PhoneScreen.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /resource:scrcpy.zip,scrcpy.zip /resource:gnirehtet.zip,gnirehtet.zip PhoneScreen.cs

if errorlevel 1 (echo. & echo СБОРКА НЕ УДАЛАСЬ & pause & exit /b 1)
echo. & echo Готово: PhoneScreen.exe & pause
