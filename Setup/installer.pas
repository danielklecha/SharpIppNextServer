// =============================================================================
// IppPrinter Inno Setup Script Logic
// =============================================================================

var
  InstallTypePage: TInputOptionWizardPage;
  ServiceAppPage: TInputOptionWizardPage;
  JobsDirPage: TInputDirWizardPage;
  ProtocolPage: TInputOptionWizardPage;
  PostProcessPage: TInputQueryWizardPage;
  CachedInstallLogPath: string;

// =============================================================================
// 1. Prerequisites & Environment Detection
// =============================================================================

function CheckRegistryForVersion10(RootKey: Integer; SubKey: string): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, SubKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      // If version starts with '10.', it is installed
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function IsDotNet10AspNetCoreInstalled(): Boolean;
var
  Hives: array[0..1] of Integer;
  I: Integer;
begin
  Result := False;
  Hives[0] := HKLM64;
  Hives[1] := HKLM32;

  for I := 0 to 1 do
  begin
    if (Hives[I] <> HKLM64) or IsWin64 then
    begin
      if CheckRegistryForVersion10(Hives[I], 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App') or
         CheckRegistryForVersion10(Hives[I], 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sdk') then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// =============================================================================
// 2. System & Process Management Helpers
// =============================================================================

function IsPortInUse(Port: string): Boolean;
var
  ResultCode: Integer;
  Command: string;
begin
  // We use cmd.exe to run netstat and findstr to check if the port is in LISTENING state.
  // Port is searched with a trailing space to prevent matching sub-ports (e.g. 6310).
  Command := '/c "netstat -ano | findstr ":' + Port + ' " | findstr LISTENING"';
  if Exec(ExpandConstant('{cmd}'), Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := (ResultCode = 0);
  end
  else
  begin
    Result := False;
  end;
end;

procedure StopIppPrinterApp();
var
  ResultCode: Integer;
begin
  // Stop service and running process synchronously in a single PowerShell call
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name ''IppPrinter'' -Force -ErrorAction SilentlyContinue; Stop-Process -Name ''IppPrinter'' -Force -ErrorAction SilentlyContinue"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// =============================================================================
// 3. Custom Wizard UI & Page Navigation
// =============================================================================

procedure InitializeWizard();
begin
  // 1. Installation Type Page (Express vs Advanced) - immediately after License page
  InstallTypePage := CreateInputOptionPage(wpLicense,
    'Select Installation Type',
    'How would you like to install IppPrinter?',
    'Select an installation option:' + #13#10#13#10 +
    '1. Express Installation (Recommended): Installs with default settings (Windows Service, HTTPS, Desktop shortcut, standard installation and jobs folders).' + #13#10#13#10 +
    '2. Advanced Installation: Customizes installation folder, execution mode, connection protocol, jobs folder, post-process action, and shortcuts.',
    True, False);
  InstallTypePage.Add('Express Installation (Recommended) - Use standard default settings');
  InstallTypePage.Add('Advanced Installation - Customize installation options');
  InstallTypePage.SelectedValueIndex := 0;

  // 2. Execution Mode Page (Windows Service vs Startup App) - after Destination Location page (wpSelectDir)
  ServiceAppPage := CreateInputOptionPage(wpSelectDir,
    'Select Execution Mode',
    'How should IppPrinter be executed?',
    'Select the execution mode:' + #13#10#13#10 +
    '1. Windows Service (Default): Runs system-wide before user logon. Isolated in Session 0 (cannot display GUI windows or open interactive GUI applications).' + #13#10#13#10 +
    '2. Startup Application: Runs automatically when a user logs in to Windows. Runs in the interactive desktop session (allows calling post-process applications with GUI).',
    True, False);
  ServiceAppPage.Add('Windows Service (Default) - System-wide background service (Session 0, no GUI)');
  ServiceAppPage.Add('Startup Application - User logon startup app (Allows launching apps with GUI)');
  ServiceAppPage.SelectedValueIndex := 0;

  // 3. Connection Protocol Page - after ServiceAppPage.ID
  ProtocolPage := CreateInputOptionPage(ServiceAppPage.ID,
    'Select Connection Protocol',
    'How should the printer be accessed?',
    'Select whether the printer should use HTTP or HTTPS.',
    True, False);
  ProtocolPage.Add('HTTPS (Port 631) - Self-signed certificate will be generated');
  ProtocolPage.Add('HTTP (Port 631) - No certificate required, less secure');
  ProtocolPage.SelectedValueIndex := 0;

  // 4. Jobs Directory Page - after ProtocolPage.ID
  JobsDirPage := CreateInputDirPage(ProtocolPage.ID,
    'Select Jobs Directory',
    'Where should the print jobs be saved?',
    'Specify the folder where print jobs should be stored, then click Next.' + #13#10 +
    'Note: C:\ProgramData is recommended for system-wide background services.',
    False,
    '');
  JobsDirPage.Add('Jobs folder:');
  JobsDirPage.Values[0] := ExpandConstant('{commonappdata}\IppPrinter\jobs');

  // 5. Post-Processing Action Page - after JobsDirPage.ID
  PostProcessPage := CreateInputQueryPage(JobsDirPage.ID,
    'Post-Processing Action',
    'Configure optional post-process command (PostProcessName & PostProcessArguments)',
    'Specify an application path and arguments to execute automatically when a print job is received.' + #13#10 +
    'The placeholder {fullName} will be replaced with the saved PDF path.' + #13#10 +
    'Leave blank (default empty) if no post-processing action is required.');
  PostProcessPage.Add('Process / Application Path (PostProcessName):', False);
  PostProcessPage.Add('Process Arguments (PostProcessArguments):', False);
  PostProcessPage.Values[0] := '';
  PostProcessPage.Values[1] := '';
end;

function IsExpressInstall(): Boolean;
begin
  Result := (InstallTypePage = nil) or (InstallTypePage.SelectedValueIndex = 0);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if IsExpressInstall() then
  begin
    if (PageID <> wpLicense) and (PageID <> InstallTypePage.ID) then
      Result := True;
  end;
end;

function IsServiceSelected(): Boolean;
begin
  if IsExpressInstall() then
    Result := True
  else
    Result := (ServiceAppPage = nil) or (ServiceAppPage.SelectedValueIndex = 0);
end;

function IsStartupAppSelected(): Boolean;
begin
  if IsExpressInstall() then
    Result := False
  else
    Result := (ServiceAppPage <> nil) and (ServiceAppPage.SelectedValueIndex = 1);
end;

function IsHttpsSelected(): Boolean;
begin
  if IsExpressInstall() then
    Result := True
  else
    Result := (ProtocolPage = nil) or (ProtocolPage.SelectedValueIndex = 0);
end;

function IsDesktopIconSelected(): Boolean;
begin
  if IsExpressInstall() then
    Result := True
  else
    Result := WizardIsTaskSelected('desktopicon');
end;

// =============================================================================
// 4. Parameter Getters for Inno Setup Code Directives
// =============================================================================

function GetProtocolScheme(Param: string): string;
begin
  if IsHttpsSelected() then
    Result := 'https'
  else
    Result := 'http';
end;

function GetJobsDir(Param: string): string;
begin
  if IsExpressInstall() or (JobsDirPage = nil) then
    Result := ExpandConstant('{commonappdata}\IppPrinter\jobs')
  else
    Result := JobsDirPage.Values[0];
end;

procedure GrantFolderPermissions(Directory: string);
var
  ResultCode: Integer;
  Params: string;
begin
  // Grant Full Control to both Local System (*S-1-5-18) for Windows Service mode
  // and BUILTIN\Users (*S-1-5-32-545) for Startup Application mode.
  Params := '"' + Directory + '" /grant *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)F /T /Q';
  Exec('icacls.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure SetupJobsDir();
begin
  ForceDirectories(GetJobsDir(''));
  GrantFolderPermissions(GetJobsDir(''));
end;

function GetPostProcessName(Param: string): string;
var
  Val: string;
begin
  if IsExpressInstall() or (PostProcessPage = nil) then
    Val := ''
  else
    Val := PostProcessPage.Values[0];
  StringChange(Val, '"', '\"');
  Result := Val;
end;

function GetPostProcessArguments(Param: string): string;
var
  Val: string;
begin
  if IsExpressInstall() or (PostProcessPage = nil) then
    Val := ''
  else
    Val := PostProcessPage.Values[1];
  StringChange(Val, '"', '\"');
  Result := Val;
end;

// =============================================================================
// 5. Logging Helpers & Setup Event Handlers
// =============================================================================

function GetInstallLogPath(Param: string): string;
var
  UserProfile: string;
begin
  if CachedInstallLogPath <> '' then
  begin
    Result := CachedInstallLogPath;
    Exit;
  end;

  UserProfile := GetEnv('USERPROFILE');
  if UserProfile = '' then
    UserProfile := ExpandConstant('{%USERPROFILE}');
  if UserProfile = '' then
    UserProfile := ExpandConstant('{userdocs}'); // fallback
  CachedInstallLogPath := AddBackslash(UserProfile) + 'IppPrinter_install_log_' + GetDateTimeString('yyyy-mm-dd_hh-nn-ss', #0, #0) + '.log';
  Result := CachedInstallLogPath;
end;

procedure MergePrinterLogToSetupLog(DestLogPath, PrinterLogPath: string);
var
  PrinterLogLines: TArrayOfString;
  Separator: TArrayOfString;
begin
  if FileExists(PrinterLogPath) then
  begin
    if LoadStringsFromFile(PrinterLogPath, PrinterLogLines) then
    begin
      SetArrayLength(Separator, 3);
      Separator[0] := '';
      Separator[1] := '--------------------------------------------------';
      Separator[2] := '--- PowerShell Add-Printer.ps1 Output ---';
      SaveStringsToUTF8File(DestLogPath, Separator, true);
      SaveStringsToUTF8File(DestLogPath, PrinterLogLines, true);
    end;
    DeleteFile(PrinterLogPath);
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet10AspNetCoreInstalled() then
  begin
    if MsgBox('This application requires the ASP.NET Core Runtime 10.0 (x64) or higher.' + #13#10 +
              'It does not appear to be installed on this system.' + #13#10#13#10 +
              'Would you like to visit the Microsoft .NET download page now?', 
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
    Exit;
  end;

  // Stop any running service or startup process immediately to release folder/file locks and free port 631
  StopIppPrinterApp();
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LogFilePathName, DestLogPath, PrinterLogPath: string;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // 1. Stop service & process synchronously
    StopIppPrinterApp();
    
    // 2. Delete existing Windows service
    Exec('sc.exe', 'delete IppPrinter', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    
    // 3. Remove existing registry Run key if Service mode selected
    if IsServiceSelected() then
    begin
      RegDeleteValue(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'IppPrinter');
      RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'IppPrinter');
    end;

    // 4. Delete the Windows firewall rule to avoid duplicates
    Exec('netsh.exe', 'advfirewall firewall delete rule name="IppPrinter"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 5. Check if port 631 is in use and show warning if it is
    if IsPortInUse('631') then
    begin
      MsgBox('Warning: Port 631 is in use by another application.' + #13#10 +
             'The printer service might not work properly if this port is not free.',
             mbInformation, MB_OK);
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    LogFilePathName := ExpandConstant('{log}');
    DestLogPath := GetInstallLogPath('');
    PrinterLogPath := ExpandConstant('{app}\printer_setup.log');
    
    // Copy the Inno Setup log to the destination log path and append printer log
    if FileExists(LogFilePathName) then
    begin
      CopyFile(LogFilePathName, DestLogPath, false);
      MergePrinterLogToSetupLog(DestLogPath, PrinterLogPath);
    end;
  end;
end;

