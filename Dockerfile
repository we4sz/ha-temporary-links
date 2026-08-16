# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore the app project directly — the solution also lists test projects,
# which are not copied into (or wanted in) the image build.
COPY ["src/TemporaryLinks.Addon/TemporaryLinks.Addon.csproj", "src/TemporaryLinks.Addon/"]
RUN dotnet restore src/TemporaryLinks.Addon/TemporaryLinks.Addon.csproj

# Copy all source files
COPY . .

# Build and publish
WORKDIR "/src/src/TemporaryLinks.Addon"
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install jq for parsing options.json
RUN apt-get update && apt-get install -y --no-install-recommends \
    jq \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published application
COPY --from=build /app/publish .

# Copy addon scripts
COPY run.sh /run.sh
RUN chmod a+x /run.sh

# Create data directory for SQLite
RUN mkdir -p /data

# Labels for Home Assistant
LABEL \
    io.hass.name="Temporary Links" \
    io.hass.description="Generate one-time-use temporary links" \
    io.hass.type="addon" \
    io.hass.version="1.1.1"

CMD ["/run.sh"]
