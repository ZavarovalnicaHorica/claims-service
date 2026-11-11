# Build context: this folder (src/ClaimsService)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached.
COPY ClaimsService.csproj ./
RUN dotnet restore ClaimsService.csproj

COPY . .
RUN dotnet publish ClaimsService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "Horica.ClaimsService.dll"]
