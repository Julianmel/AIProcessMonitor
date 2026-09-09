@echo off
chcp 65001 >nul
echo Compilando AIProcessMonitor.exe nativo...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /out:AIProcessMonitor.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll /resource:public\index.html,index.html Program.cs
if %ERRORLEVEL% equ 0 (
    echo.
    echo [SUCESSO] AIProcessMonitor.exe gerado com sucesso!
) else (
    echo.
    echo [ERRO] Falha na compilação.
)
pause
