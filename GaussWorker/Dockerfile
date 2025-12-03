# Используем образ SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем csproj и восстанавливаем зависимости
COPY GaussWorker.csproj .
RUN dotnet restore

# Копируем остальные файлы и собираем
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Финальный образ с runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# Копируем собранное приложение
COPY --from=build /app/publish .

# Команда запуска (порт будет передан через CMD)
ENTRYPOINT ["dotnet", "GaussWorker.dll"]