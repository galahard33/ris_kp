# Используем образ SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем csproj и восстанавливаем зависимости
COPY GaussWebApp.csproj .
RUN dotnet restore

# Копируем остальные файлы и собираем
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Финальный образ с runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Устанавливаем переменные окружения
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:80
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Создаем пользователя без привилегий для безопасности
RUN adduser -u 1000 --disabled-password --gecos "" appuser && \
    chown -R appuser:appuser /app
USER appuser

# Копируем собранное приложение
COPY --from=build --chown=appuser:appuser /app/publish .

# Создаем папку для файлов
RUN mkdir -p /app/data && chown appuser:appuser /app/data

# Открываем только HTTP порт
EXPOSE 80

# Запускаем приложение
ENTRYPOINT ["dotnet", "GaussWebApp.dll"]