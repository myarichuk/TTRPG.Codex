#!/bin/bash
dotnet publish ../src/Codex.Web/Codex.Web.csproj -c Release -r linux-x64 -o ../dist/linux -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true
