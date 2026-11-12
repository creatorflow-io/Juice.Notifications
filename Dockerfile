# Juice.Notifications: the centralized service (API and stage workers). Build from services/notifications:
#   docker build -t juice-notifications .
# Released Juice packages come from nuget.org. To restore pre-release ones from the GitHub feed too, pass its credentials as build secrets:
#   docker build --secret id=gh_user,env=GITHUB_PACKAGE_USERNAME --secret id=gh_token,env=GITHUB_PACKAGE_TOKEN -t juice-notifications .
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props nuget.config ./
COPY src/ src/
# Without the secrets the GitHub feed can't be read at all (NuGet refuses empty credentials), so restore uses nuget.org alone.
RUN --mount=type=secret,id=gh_user,required=false --mount=type=secret,id=gh_token,required=false \
    if [ -s /run/secrets/gh_token ]; then \
      export GITHUB_PACKAGE_USERNAME="$(cat /run/secrets/gh_user)" GITHUB_PACKAGE_TOKEN="$(cat /run/secrets/gh_token)"; \
    else \
      printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" /></packageSources></configuration>\n' > nuget.config; \
    fi && \
    dotnet publish src/Juice.Notifications.App -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.24
# icu-libs: host names in targets and the allowlist go through IDN mapping, which needs ICU.
# krb5-libs: Npgsql probes for GSSAPI at start and logs an error without it.
RUN apk add --no-cache icu-libs krb5-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=12 CMD wget -q -O - http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Juice.Notifications.App.dll"]
