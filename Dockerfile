# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    DOTNET_CLI_HOME=/tmp/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_PACKAGES=/app/.nuget/packages

WORKDIR /app
RUN mkdir -p "$NUGET_PACKAGES" && chown -R "$APP_UID:$APP_UID" /app
COPY --chown=$APP_UID:$APP_UID . .

USER $APP_UID
RUN dotnet restore "Agentic Patterns.slnx" \
    && dotnet build "Agentic Patterns.slnx" --no-restore \
    && dotnet nuget locals http-cache --clear

EXPOSE 5080
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5080/api/patterns >/dev/null || exit 1

ENTRYPOINT ["dotnet", "run", "--no-build", "--project", "PatternExplorer/PatternExplorer.csproj"]
