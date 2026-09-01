; Установщик Now Playing Taskbar Widget с мостом к браузеру.
; Собирать: dotnet publish -c Release -o publish  затем  ISCC.exe setup.iss

#define MyAppName "Now Playing Taskbar Widget (Yandex Browser)"
#define MyAppShortName "NowPlayingWidget"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "mcdima0001"
#define MyAppURL "https://github.com/mcdima0001/now-playing-taskbar-widget-YAbrowser"
#define MyAppExeName "SpotifyTaskbarWidget.exe"

[Setup]
; Свой AppId: установка рядом с оригинальным виджетом не должна затирать его
AppId={{3F8B27D4-9C15-4A6E-B8D2-7E519A0C4F63}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={userpf}\NowPlayingWidgetYA
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=NowPlayingWidget-YAbrowser-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutoStartTask=Запускать при входе в Windows
ru.OpenExtDir=Открыть папку с расширением для браузера
ru.ExtCaption=Расширение для браузера
ru.ExtDesc=Последний шаг - его нужно поставить вручную
ru.ExtBody=Яндекс.Браузер не сообщает Windows, что играет, поэтому трек виджету передаёт расширение.%n%nКак поставить:%n%n1. Откройте browser://extensions (в Chrome и Edge - chrome://extensions).%n2. Включите "Режим разработчика".%n3. Нажмите "Загрузить распакованное расширение" и выберите папку:%n%n%1%n%nБез расширения виджет всё равно работает - показывает Spotify, Телеграм и другие приложения, которые сообщают о треке самой Windows.
en.AutoStartTask=Start with Windows
en.OpenExtDir=Open the browser extension folder
en.ExtCaption=Browser extension
en.ExtDesc=One last step - it has to be loaded by hand
en.ExtBody=Yandex Browser never tells Windows what is playing, so a browser extension feeds the track to the widget.%n%nHow to load it:%n%n1. Open browser://extensions (chrome://extensions in Chrome and Edge).%n2. Turn on "Developer mode".%n3. Click "Load unpacked" and pick this folder:%n%n%1%n%nWithout the extension the widget still works for Spotify, Telegram and anything else that reports playback to Windows itself.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTask}"

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "bridge-extension\*"; DestDir: "{app}\bridge-extension"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#MyAppShortName}"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
Filename: "{win}\explorer.exe"; Parameters: """{app}\bridge-extension"""; \
    Description: "{cm:OpenExtDir}"; Flags: nowait postinstall skipifsilent

; taskkill по имени процесса здесь был бы опасен: он снимает ЛЮБУЮ копию
; виджета, в том числе установленную в другую папку или собранную вручную.
; Закрытием занимается CloseApplications - через Restart Manager он трогает
; только те процессы, которые держат файлы из папки установки.

[Code]
var
  ExtPage: TOutputMsgWizardPage;

// Приложению нужен .NET 8 Desktop Runtime; если его нет, тянем с сайта
// Microsoft. Без сети установка всё равно продолжится - виджет предупредит сам
function NeedsDotNet(): Boolean;
var
  RC: Integer;
begin
  Result := not (Exec('cmd.exe',
    '/c dotnet --list-runtimes | findstr /C:"Microsoft.WindowsDesktop.App 8." >nul',
    '', SW_HIDE, ewWaitUntilTerminated, RC) and (RC = 0));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RC: Integer;
begin
  Result := '';
  if NeedsDotNet() then
  begin
    try
      DownloadTemporaryFile(
        'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
        'windowsdesktop-runtime.exe', '', nil);
      Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, RC);
    except
      // нет сети или загрузка не удалась
    end;
  end;
end;

procedure InitializeWizard();
begin
  // Текст пока пустой: путь к папке подставляется при показе страницы.
  // Разворачивать {app} здесь нельзя - на этом шаге константа ещё не
  // инициализирована, Inno падает с ошибкой (в тихом режиме - молча зависает)
  ExtPage := CreateOutputMsgPage(wpInfoAfter,
    ExpandConstant('{cm:ExtCaption}'),
    ExpandConstant('{cm:ExtDesc}'),
    '');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (ExtPage <> nil) and (CurPageID = ExtPage.ID) then
    ExtPage.MsgLabel.Caption :=
      FmtMessage(ExpandConstant('{cm:ExtBody}'), [ExpandConstant('{app}\bridge-extension')]);
end;
