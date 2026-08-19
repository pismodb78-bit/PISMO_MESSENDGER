; ============================================================
;  PISMO — скрипт установщика для Inno Setup 6
;  Даёт мастер установки / обновления / удаления (как просили).
;
;  Как собрать инсталлятор:
;   1. Установить Inno Setup 6:  https://jrsoftware.org/isdl.php
;   2. Опубликовать приложение (из папки PISMO):
;        dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false
;      (выходная папка: PISMO\bin\Release\net8.0-windows\win-x64\publish)
;   3. Открыть этот .iss в Inno Setup и нажать Build (Ctrl+F9),
;      либо в командной строке:
;        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\PISMO.iss
;   4. Готовый установщик появится в installer\Output\PISMO-Setup-1.0.0.exe
;
;  Обновление: запуск нового установщика поверх старого автоматически
;  обновит версию (тот же AppId). Удаление — через «Программы и компоненты»
;  Windows или ярлык «Удалить PISMO» в меню Пуск.
; ============================================================

#define MyAppName "PISMO"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "PISMO"
#define MyAppExeName "PISMO.exe"
; Папка с результатом dotnet publish (относительно этого .iss):
#define PublishDir "..\PISMO\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; AppId уникален для продукта — НЕ меняйте между версиями, иначе обновление
; будет ставиться как отдельная программа вместо апгрейда.
AppId={{8F3C2A7E-9D14-4B6A-AE12-7C0F1A2B3C4D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PISMO
DefaultGroupName=PISMO
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
SetupIconFile=..\PISMO\pismo.ico
OutputDir=Output
OutputBaseFilename=PISMO-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Установка для всех пользователей — нужны права администратора.
PrivilegesRequired=admin

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\PISMO"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить PISMO"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PISMO"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить PISMO"; Flags: nowait postinstall skipifsilent

; --- Проверка наличия WebView2 Runtime (нужен для звонков) ---
; Если у пользователя нет WebView2 Runtime, звонки не заработают.
; Раскомментируйте блок ниже и положите рядом MicrosoftEdgeWebview2Setup.exe
; (бесплатный bootstrap от Microsoft), чтобы ставить его автоматически.
;
; [Files]
; Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
; [Run]
; Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
;   StatusMsg: "Установка компонента WebView2..."; Flags: waituntilterminated
