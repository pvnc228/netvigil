FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["NetVigil.Agent/NetVigil.Agent.csproj", "NetVigil.Agent/"]
COPY ["NetVigil.Shared/NetVigil.Shared.csproj", "NetVigil.Shared/"]

RUN dotnet restore "NetVigil.Agent/NetVigil.Agent.csproj"

COPY . .
WORKDIR "/src/NetVigil.Agent"
RUN dotnet build "NetVigil.Agent.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NetVigil.Agent.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NetVigil.Agent.dll"]