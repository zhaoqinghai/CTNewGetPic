@echo off
set targetPath=.\Publish

REM 创建目标文件夹（如果不存在）
if not exist %targetPath% (
    mkdir %targetPath%
)

if exist %targetPath% (
    rd /s /q %targetPath%
)

REM 发布 CTNewGetPic 到目标文件夹
dotnet publish .\GetPic\CTNewGetPic.csproj --self-contained -r win-x64 -c Release -o %targetPath%

REM 发布 ProjectB 到目标文件夹
dotnet publish .\GetPicShell\GetPicShell.csproj -r win-x64 -c Release -o %targetPath%

echo.|dotNET_Reactor -project "CTNewGetPic.nrproj"

xcopy obfuscators\* Publish /s /e /y

echo 发布完成，所有文件已复制到 %targetPath%
