Option Explicit

Dim shell, files, directory, powershell, command, exitCode
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
directory = files.GetParentFolderName(WScript.ScriptFullName)
powershell = shell.ExpandEnvironmentStrings("%ProgramFiles%\PowerShell\7\pwsh.exe")

' Hide the console at process creation, before PowerShell parses WindowStyle.
' Wait so Task Scheduler still tracks the run and receives its exit code.
command = Chr(34) & powershell & Chr(34) & _
    " -NoProfile -NonInteractive -WindowStyle Hidden -File " & _
    Chr(34) & files.BuildPath(directory, "run-sync.ps1") & Chr(34)
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
