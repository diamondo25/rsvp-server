rmdir /q /s BinSvr_Published
dotnet publish WvsBeta_REVAMP.sln -r win-x64 --self-contained false -c Release -o BinSvr_Published -p:DebugType=embedded
xcopy  /E /I /Y WvsBeta.Database\evolutions BinSvr_Published\evolutions

pause