FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AgrocultivoWebSync.csproj", "./"]
RUN dotnet restore "AgrocultivoWebSync.csproj"

COPY . .
RUN dotnet publish "AgrocultivoWebSync.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

ENTRYPOINT ["dotnet", "AgrocultivoWebSync.dll"]