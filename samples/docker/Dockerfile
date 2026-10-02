# Multi-stage build for the Nordea transaction app.
# Adjust the project path (src/NordeaApp) to the real solution layout.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/NordeaApp/NordeaApp.csproj \
 && dotnet publish src/NordeaApp/NordeaApp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_EnableDiagnostics=0 \
    App__DataDir=/data
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=60s --timeout=5s --retries=3 \
  CMD ["dotnet", "NordeaApp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "NordeaApp.dll"]
