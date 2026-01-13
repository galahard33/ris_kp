FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY GaussWebApp.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish

# Финальный образ с runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:80
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Создаем пользователя без привилегий для безопасности
RUN adduser -u 1000 --disabled-password --gecos "" appuser && \
    chown -R appuser:appuser /app
USER appuser

COPY --from=build --chown=appuser:appuser /app/publish .

RUN mkdir -p /app/data && chown appuser:appuser /app/data

EXPOSE 80

ENTRYPOINT ["dotnet", "GaussWebApp.dll"]