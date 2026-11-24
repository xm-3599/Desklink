@echo off
setlocal
pushd "%~dp0"
set "DESKLINK_CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%DESKLINK_CSC%" set "DESKLINK_CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%DESKLINK_CSC%" /nologo /utf8output /codepage:65001 /optimize+ /target:winexe /platform:anycpu /out:Desklink.exe /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll /resource:index.html,index.html /resource:ui.css,ui.css /resource:ui.js,ui.js /resource:mark.svg,mark.svg /resource:video.js,video.js Host.cs Server.cs Desktop.cs NetworkPath.cs Video.cs
set "DESKLINK_RESULT=%ERRORLEVEL%"
popd
exit /b %DESKLINK_RESULT%
