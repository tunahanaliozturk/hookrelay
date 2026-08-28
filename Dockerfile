# One Dockerfile for all four services. They share a solution and a dependency graph, so building them
# from separate files would mean four copies of the same restore layer going stale independently.
ARG PROJECT

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG PROJECT
WORKDIR /src

# Restore first, against the manifests only, so a source change does not invalidate the package layer.
# .editorconfig comes along because analyzer severity lives in it, including the rules that are turned
# off for EF's generated migrations. Without it the image build fails on code the tooling wrote.
COPY global.json Directory.Build.props Directory.Packages.props HookRelay.slnx .editorconfig ./
COPY src/HookRelay.Api/HookRelay.Api.csproj src/HookRelay.Api/
COPY src/HookRelay.AppHost/HookRelay.AppHost.csproj src/HookRelay.AppHost/
COPY src/HookRelay.ChaosReceiver/HookRelay.ChaosReceiver.csproj src/HookRelay.ChaosReceiver/
COPY src/HookRelay.Domain/HookRelay.Domain.csproj src/HookRelay.Domain/
COPY src/HookRelay.Infrastructure/HookRelay.Infrastructure.csproj src/HookRelay.Infrastructure/
COPY src/HookRelay.Relay/HookRelay.Relay.csproj src/HookRelay.Relay/
COPY src/HookRelay.ServiceDefaults/HookRelay.ServiceDefaults.csproj src/HookRelay.ServiceDefaults/
COPY src/HookRelay.Worker/HookRelay.Worker.csproj src/HookRelay.Worker/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
    --no-restore \
    --configuration Release \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

# Nothing here needs to write to its own filesystem or run as root.
RUN useradd --uid 64198 --create-home --shell /usr/sbin/nologin hookrelay
USER 64198

COPY --from=build /app .

ARG PROJECT
ENV ENTRYPOINT_DLL="${PROJECT}.dll"
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"${ENTRYPOINT_DLL}\""]
