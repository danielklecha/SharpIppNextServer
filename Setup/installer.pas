var
  JobsDirPage: TInputDirWizardPage;
  CachedInstallLogPath: string;

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
begin
  // 1. Check ASP.NET Core Runtime 10 in 32-bit registry view (WOW6432Node on 64-bit Windows)
  if CheckRegistryForVersion10(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App') then
  begin
    Result := True;
    Exit;
  end;

  // 2. Check .NET SDK 10 in 32-bit registry view (WOW6432Node on 64-bit Windows)
  if CheckRegistryForVersion10(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sdk') then
  begin
    Result := True;
    Exit;
  end;

  if IsWin64 then
  begin
    // 3. Check ASP.NET Core Runtime 10 in 64-bit registry view
    if CheckRegistryForVersion10(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App') then
    begin
      Result := True;
      Exit;
    end;

    // 4. Check .NET SDK 10 in 64-bit registry view
    if CheckRegistryForVersion10(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sdk') then
    begin
      Result := True;
      Exit;
    end;
  end;

  Result := False;
end;

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
    // If findstr succeeds (ResultCode = 0), the port is in use.
    Result := (ResultCode = 0);
  end
  else
  begin
    Result := False;
  end;
end;

procedure StopIppPrinterService();
var
  ResultCode: Integer;
begin
  // Stop service synchronously using PowerShell Stop-Service
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name ''IppPrinter'' -Force -ErrorAction SilentlyContinue"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

  // Stop the running service immediately to release folder/file locks and free port 631
  StopIppPrinterService();
end;

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

procedure InitializeWizard();
begin
  JobsDirPage := CreateInputDirPage(wpSelectDir,
    'Select Jobs Directory',
    'Where should the print jobs be saved?',
    'Specify the folder where print jobs should be stored, then click Next.' + #13#10 +
    'Note: C:\ProgramData is recommended for system-wide background services.',
    False,
    '');
  JobsDirPage.Add('Jobs folder:');
  JobsDirPage.Values[0] := ExpandConstant('{commonappdata}\IppPrinter\jobs');
end;

function GetJobsDir(Param: string): string;
begin
  if JobsDirPage <> nil then
    Result := JobsDirPage.Values[0]
  else
    Result := ExpandConstant('{commonappdata}\IppPrinter\jobs');
end;

function EscapeJsonString(Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure GrantFolderPermissions(Directory: string);
var
  ResultCode: Integer;
  Params: string;
begin
  Params := '"' + Directory + '" /grant *S-1-5-18:(OI)(CI)F /T /Q';
  Exec('icacls.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure UpdateAppSettingsJson();
var
  JsonPath: string;
  Lines: TArrayOfString;
  I, J: Integer;
  EscapedJobsDir: string;
  PrinterIndex: Integer;
  JobsPathIndex: Integer;
  NewLines: TArrayOfString;
begin
  JsonPath := ExpandConstant('{app}\appsettings.json');
  if not FileExists(JsonPath) then
    Exit;

  if LoadStringsFromFile(JsonPath, Lines) then
  begin
    JobsPathIndex := -1;
    PrinterIndex := -1;
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      if Pos('"JobsPath":', Lines[I]) > 0 then
        JobsPathIndex := I
      else if Pos('"Printer":', Lines[I]) > 0 then
        PrinterIndex := I;
    end;

    EscapedJobsDir := EscapeJsonString(GetJobsDir(''));
    ForceDirectories(GetJobsDir(''));
    GrantFolderPermissions(GetJobsDir(''));

    if JobsPathIndex <> -1 then
    begin
      Lines[JobsPathIndex] := '    "JobsPath": "' + EscapedJobsDir + '",';
      SaveStringsToUTF8File(JsonPath, Lines, False);
    end
    else if PrinterIndex <> -1 then
    begin
      SetArrayLength(NewLines, GetArrayLength(Lines) + 1);
      for J := 0 to PrinterIndex do
      begin
        NewLines[J] := Lines[J];
      end;
      NewLines[PrinterIndex + 1] := '    "JobsPath": "' + EscapedJobsDir + '",';
      for J := PrinterIndex + 1 to GetArrayLength(Lines) - 1 do
      begin
        NewLines[J + 1] := Lines[J];
      end;
      SaveStringsToUTF8File(JsonPath, NewLines, False);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LogFilePathName, DestLogPath, PrinterLogPath: string;
  PrinterLogLines: TArrayOfString;
  Separator: TArrayOfString;
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // 1. Stop service synchronously using PowerShell Stop-Service
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Stop-Service -Name ''IppPrinter'' -Force -ErrorAction SilentlyContinue"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    
    // 2. Delete the Windows service
    Exec('sc.exe', 'delete IppPrinter', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    
    // 3. Delete the Windows firewall rule to avoid duplicates
    Exec('netsh.exe', 'advfirewall firewall delete rule name="IppPrinter"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 4. Check if port 631 is in use and show warning if it is
    if IsPortInUse('631') then
    begin
      MsgBox('Warning: Port 631 is in use by another application.' + #13#10 +
             'The printer service might not work properly if this port is not free.',
             mbInformation, MB_OK);
    end;
  end
  else if CurStep = ssDone then
  begin
    LogFilePathName := ExpandConstant('{log}');
    DestLogPath := GetInstallLogPath('');
    PrinterLogPath := ExpandConstant('{app}\printer_setup.log');
    
    // Copy the Inno Setup log to the destination log path
    if FileExists(LogFilePathName) then
    begin
      CopyFile(LogFilePathName, DestLogPath, false);
      
      // If printer log exists, append its contents to the installer log
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
  end;
end;
