# Multi-stage. The SDK image is ~800MB and contains a compiler, a package manager and a shell full of
# build tooling; none of that belongs in something that runs in production, where every extra binary
# is attack surface that has to be patched.
#
# Built from the repository root, not from this directory, because the projects reference each other
# and Directory.Packages.props lives at the top:
#   docker build -f src/OrderProcessing.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0.200 AS build
WORKDIR /src

# Manifests first, restore second, sources third. Docker caches per layer, so as long as no
# dependency has changed the restore layer is reused - and restore is by far the slowest step.
# Copying everything at once would re-restore on every one-character source edit.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/OrderProcessing.Contracts/*.csproj    src/OrderProcessing.Contracts/
COPY src/OrderProcessing.Persistence/*.csproj  src/OrderProcessing.Persistence/
COPY src/OrderProcessing.Messaging/*.csproj    src/OrderProcessing.Messaging/
COPY src/OrderProcessing.Api/*.csproj          src/OrderProcessing.Api/
COPY src/OrderProcessing.Worker/*.csproj       src/OrderProcessing.Worker/
RUN dotnet restore src/OrderProcessing.Api/OrderProcessing.Api.csproj

COPY src/ src/
RUN dotnet publish src/OrderProcessing.Api/OrderProcessing.Api.csproj     -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.7 AS runtime
WORKDIR /app

# Two additions, both deliberate:
#
#   libfontconfig1 - QuestPDF renders through SkiaSharp, which is native code and needs fontconfig.
#     Without it the failure appears at the first render as a dead-lettered message rather than as a
#     missing dependency, which is a long way from the cause.
#
#   curl - the runtime image ships neither curl nor wget, so a HEALTHCHECK written against either
#     fails every time and the container is reported unhealthy while being perfectly fine. That in
#     turn blocks anything waiting on `depends_on: condition: service_healthy`.
RUN apt-get update  && apt-get install -y --no-install-recommends libfontconfig1 curl  && rm -rf /var/lib/apt/lists/*

# Non-root. The base image ships an unprivileged 'app' user for exactly this; a container that runs
# as root shares that root with the host kernel.
USER $APP_UID

COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OrderProcessing.Api.dll"]
