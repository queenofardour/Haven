FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Haven.Core/ src/Haven.Core/
COPY src/Haven.Relay/ src/Haven.Relay/
COPY src/Haven/Native/Upstream/Wire/HMSyncWireTypes.cs src/Haven/Native/Upstream/Wire/HMSyncWireTypes.cs
RUN dotnet publish src/Haven.Relay/Haven.Relay.csproj -c Release -o /app -p:UseAppHost=false -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE THIRD_PARTY_NOTICES.md ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER 1654
ENTRYPOINT ["dotnet", "Haven.Relay.dll"]
