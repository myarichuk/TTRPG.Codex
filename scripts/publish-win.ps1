param(
    [string]$project = "../src/Codex.Web/Codex.Web.csproj"
)
dotnet publish $project -c Release -r win-x64 -o ../dist/win -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true
