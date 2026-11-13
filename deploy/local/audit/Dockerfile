# The local stack's audit service (docker-compose.yml). Build context: deploy/local/audit.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Juice.Notifications.LocalAudit.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-alpine3.24
# krb5-libs: Npgsql probes for GSSAPI at start and logs an error without it.
RUN apk add --no-cache icu-libs krb5-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    Kestrel__Endpoints__Grpc__Url=http://+:8080 \
    Kestrel__Endpoints__Grpc__Protocols=Http2 \
    Kestrel__Endpoints__Health__Url=http://+:8081 \
    Kestrel__Endpoints__Health__Protocols=Http1 \
    Logging__LogLevel__Microsoft.AspNetCore=Warning
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080 8081
HEALTHCHECK --interval=5s --timeout=3s --retries=30 CMD wget -q -O - http://127.0.0.1:8081/health || exit 1
ENTRYPOINT ["dotnet", "Juice.Notifications.LocalAudit.dll"]
