FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["NetVigil.Server/NetVigil.Server.csproj", "NetVigil.Server/"]
COPY ["NetVigil.Shared/NetVigil.Shared.csproj", "NetVigil.Shared/"]

RUN dotnet restore "NetVigil.Server/NetVigil.Server.csproj"

COPY . .
WORKDIR "/src/NetVigil.Server"

RUN dotnet build "NetVigil.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NetVigil.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "NetVigil.Server.dll"]