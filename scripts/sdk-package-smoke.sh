#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d -t helpaffe-sdk-smoke.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/feed" "$WORK/consumer"
dotnet pack "$ROOT/src/Helpaffe.Sdk/Helpaffe.Sdk.csproj" \
  -c Release -o "$WORK/feed"

PACKAGE="$(find "$WORK/feed" -maxdepth 1 -name 'Helpaffe.Sdk.*.nupkg' -print -quit)"
test -n "$PACKAGE"
unzip -Z1 "$PACKAGE" | grep -qx 'lib/net10.0/Helpaffe.Sdk.dll'
unzip -Z1 "$PACKAGE" | grep -qx 'README.md'
VERSION="$(basename "$PACKAGE" .nupkg)"
VERSION="${VERSION#Helpaffe.Sdk.}"

cd "$WORK/consumer"
dotnet new console --framework net10.0 --no-restore
dotnet add package Helpaffe.Sdk --version "$VERSION" --source "$WORK/feed"
cat > Program.cs <<'CSHARP'
using Helpaffe.Sdk;

using var httpClient = new HttpClient();
var client = new HelpaffeProductClient(
    httpClient,
    new Uri("https://helpaffe.example.test"),
    "hfp_example");
if (client is null)
    throw new InvalidOperationException("The SDK could not be constructed.");

try
{
    _ = client.CreateTicketAsync(
        new CreateProductTicket("user-1", "Example", "user@example.test", "Subject", "Message"),
        "");
    throw new InvalidOperationException("Invalid request accepted.");
}
catch (ArgumentException)
{
    Console.WriteLine("Helpaffe.Sdk works from a clean consumer.");
}
CSHARP
dotnet run --configuration Release
