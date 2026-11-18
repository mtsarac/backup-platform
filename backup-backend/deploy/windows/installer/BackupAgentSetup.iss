; Windows setup wizard for the backup agent (Inno Setup 6.3 or newer).
;
;   ISCC.exe /DAppVersion=1.2.3 /DFileVersion=1.2.3.0 [/Fbackup-agent-win-x64-setup-1.2.3] BackupAgentSetup.iss
;
; The wizard asks only for the one-time enrollment key and the computer name, then runs the existing ..\install.ps1 non-interactively.
; It writes no installation logic of its own. The server address is not asked: the backend appends it to the file when it serves it
; (see AgentDistributionEndpoints.SetupTrailerPrefix), and this wizard reads it back from its own file.
#ifndef AppVersion
  #error Pass /DAppVersion=<semantic version> (and /DFileVersion=<n.n.n.n>)
#endif
#ifndef FileVersion
  #error Pass /DFileVersion=<n.n.n.n>
#endif

[Setup]
AppName=Backup Platform
AppVersion={#AppVersion}
AppPublisher=Backup Platform
VersionInfoVersion={#FileVersion}
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
ShowLanguageDialog=auto
DisableReadyPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=backup-agent-win-x64-setup

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\install.ps1"; Flags: dontcopy

[CustomMessages]
en.PageTitle=Add this computer
en.PageDescription=Enter the one-time key from the Add backup computer window.
en.KeyPrompt=One-time enrollment key:
en.NamePrompt=Computer name shown in the list:
en.BadKey=The enrollment key is not valid. Copy the whole key from the Add backup computer window.
en.BadName=Enter a computer name of 1 to 255 characters without quotes or control characters.
en.NoServer=This installer does not know your backup server. Download it again from the Add backup computer window of your backup server.
en.Installing=Installing the backup program and registering this computer...
en.Failed=Setup could not finish:
en.DoneHeading=This computer is added
en.DoneText=The backup program is installed and running. This computer appears as Online in Computers after it connects, usually within a minute.
tr.PageTitle=Bu bilgisayarı ekleyin
tr.PageDescription=Yedekleme bilgisayarı ekle penceresindeki tek kullanımlık anahtarı girin.
tr.KeyPrompt=Tek kullanımlık kayıt anahtarı:
tr.NamePrompt=Listede görünecek bilgisayar adı:
tr.BadKey=Kayıt anahtarı geçerli değil. Yedekleme bilgisayarı ekle penceresindeki anahtarın tamamını kopyalayın.
tr.BadName=Tırnak ve denetim karakteri içermeyen, 1 ile 255 karakter arasında bir bilgisayar adı girin.
tr.NoServer=Bu kurulum programı yedekleme sunucunuzu bilmiyor. Yedekleme sunucunuzdaki Yedekleme bilgisayarı ekle penceresinden yeniden indirin.
tr.Installing=Yedekleme programı kuruluyor ve bu bilgisayar kaydediliyor...
tr.Failed=Kurulum tamamlanamadı:
tr.DoneHeading=Bu bilgisayar eklendi
tr.DoneText=Yedekleme programı kuruldu ve çalışıyor. Bağlandıktan sonra, genellikle bir dakika içinde, bu bilgisayar Bilgisayarlar listesinde Çevrimiçi görünür.

[Messages]
en.FinishedHeadingLabel=This computer is added
en.FinishedLabelNoIcons=The backup program is installed and running. This computer appears as Online in Computers after it connects, usually within a minute.
tr.FinishedHeadingLabel=Bu bilgisayar eklendi
tr.FinishedLabelNoIcons=Yedekleme programı kuruldu ve çalışıyor. Bağlandıktan sonra, genellikle bir dakika içinde, bu bilgisayar Bilgisayarlar listesinde Çevrimiçi görünür.

[Code]
const
  ServerPrefix = #10'BackupPlatformServer=';

var
  InputPage: TInputQueryWizardPage;
  ServerUrl: String;

// The backend appends "\nBackupPlatformServer=<https url>\n" to the file it serves; anything else means the file did not come from the server.
function ReadEmbeddedServer(): String;
var
  Raw: AnsiString;
  Text: String;
  I, Stop, Last: Integer;
begin
  Result := '';
  if not LoadStringFromFile(ExpandConstant('{srcexe}'), Raw) then Exit;
  Last := Length(Raw);
  if (Last = 0) or (Raw[Last] <> #10) then Exit;
  Stop := Last - 2200;
  if Stop < 1 then Stop := 1;
  for I := Last - Length(ServerPrefix) downto Stop do
    if Copy(Raw, I, Length(ServerPrefix)) = ServerPrefix then
    begin
      Text := Copy(Raw, I + Length(ServerPrefix), Last - I - Length(ServerPrefix));
      if (Pos('https://', Text) = 1) and (Pos(#10, Text) = 0) and (Pos('"', Text) = 0) and (Pos('''', Text) = 0) and (Pos(' ', Text) = 0) then Result := Text;
      Exit;
    end;
end;

// 32 hex digits, a dot and 43 URL-safe characters; install.ps1 repeats the full check.
function IsKey(const Key: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(Key) = 76) and (Key[33] = '.');
  if not Result then Exit;
  for I := 1 to 76 do
  begin
    C := Key[I];
    if I = 33 then Continue;
    if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or ((I > 33) and ((C = '_') or (C = '-')))) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// Quotes are refused because the values are passed inside a single-quoted PowerShell string.
function IsName(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := (Length(Name) >= 1) and (Length(Name) <= 255) and (Trim(Name) <> '');
  for I := 1 to Length(Name) do
    if (Name[I] < ' ') or (Name[I] = '"') or (Name[I] = '''') then Result := False;
end;

function InputError(const Key, Name: String): String;
begin
  Result := '';
  if not IsKey(Key) then Result := CustomMessage('BadKey')
  else if not IsName(Name) then Result := CustomMessage('BadName');
end;

function InitializeSetup(): Boolean;
begin
  ServerUrl := ReadEmbeddedServer();
  Result := ServerUrl <> '';
  if not Result then SuppressibleMsgBox(CustomMessage('NoServer'), mbError, MB_OK, IDOK);
end;

procedure InitializeWizard();
begin
  InputPage := CreateInputQueryPage(wpWelcome, CustomMessage('PageTitle'), CustomMessage('PageDescription'), '');
  InputPage.Add(CustomMessage('KeyPrompt'), True);
  InputPage.Add(CustomMessage('NamePrompt'), False);
  // Command-line values also drive /VERYSILENT runs, where no page is shown.
  InputPage.Values[0] := ExpandConstant('{param:Key|}');
  InputPage.Values[1] := ExpandConstant('{param:Name|}');
  if InputPage.Values[1] = '' then InputPage.Values[1] := GetComputerNameString;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Message: String;
begin
  Result := True;
  if CurPageID <> InputPage.ID then Exit;
  Message := InputError(InputPage.Values[0], InputPage.Values[1]);
  if Message <> '' then
  begin
    SuppressibleMsgBox(Message, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure Fail(const Message: String);
begin
  Log('Setup failed: ' + Message);
  SuppressibleMsgBox(CustomMessage('Failed') + #13#10#13#10 + Message, mbCriticalError, MB_OK, IDOK);
  Abort;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Key, Name, Message, Parameters, Language: String;
  ErrorLines: TArrayOfString;
  Code: Integer;
begin
  if CurStep <> ssInstall then Exit;
  Key := InputPage.Values[0];
  Name := InputPage.Values[1];
  Message := InputError(Key, Name);
  if Message <> '' then Fail(Message);
  if ActiveLanguage = 'tr' then Language := 'tr' else Language := 'en';
  WizardForm.StatusLabel.Caption := CustomMessage('Installing');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  ExtractTemporaryFile('install.ps1');
  DeleteFile(ExpandConstant('{tmp}\install-error.txt'));
  // The error text goes to a file relative to the working directory so no path has to be quoted into the command.
  Parameters := '-NoProfile -ExecutionPolicy Bypass -Command "try { & ''.\install.ps1'' -NonInteractive -Server ''' + ServerUrl +
    ''' -Token ''' + Key + ''' -AgentName ''' + Name + ''' -Language ' + Language +
    ' *> $null } catch { Set-Content -Path ''install-error.txt'' -Value $_.Exception.Message -Encoding UTF8 ; exit 1 } ; exit 0"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Fail(SysErrorMessage(Code));
  if Code <> 0 then
  begin
    if LoadStringsFromFile(ExpandConstant('{tmp}\install-error.txt'), ErrorLines) and (GetArrayLength(ErrorLines) > 0) then Message := ErrorLines[0]
    else Message := SysErrorMessage(Code);
    Fail(Message);
  end;
end;
