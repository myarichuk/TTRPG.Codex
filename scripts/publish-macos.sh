#!/bin/bash
dotnet publish ../src/Codex.Web/Codex.Web.csproj -c Release -r osx-arm64 -o ../dist/macos -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true
