# Why this file exists: Render has no native .NET runtime, so this service deploys as a
# Docker image, like every backend in this project.
#
# JS/TS vs C#: there are FOUR stages built from one file.
#   restore - dependencies only, so the (slow) NuGet download is cached until a .csproj
#             changes, not re-run on every code edit (the analogue of copying
#             package.json before running `npm ci`)
#   test    - build and run the unit tests (`docker build --target test`)
#   publish - compile the app into a folder of DLLs
#   runtime - the small image Render runs: the ASP.NET runtime plus those DLLs (the
#             last stage, so a plain `docker build .` produces it)
# Unlike Go there is no single static binary: the image carries the .NET runtime. Unlike
# Node it carries no node_modules and no source.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY *.slnx ./
COPY src/LinkApi/LinkApi.csproj src/LinkApi/
COPY tests/LinkApi.Tests/LinkApi.Tests.csproj tests/LinkApi.Tests/
RUN dotnet restore

FROM restore AS test
COPY src ./src
COPY tests ./tests
RUN dotnet test --no-restore --nologo

FROM restore AS publish
COPY src ./src
RUN dotnet publish src/LinkApi/LinkApi.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /out .
# The image defines APP_UID for an unprivileged user: if the app were ever compromised,
# it can't write to the system.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "LinkApi.dll"]
