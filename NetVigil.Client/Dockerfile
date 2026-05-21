FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["NetVigil.Client/NetVigil.Client.csproj", "NetVigil.Client/"]
COPY ["NetVigil.Shared/NetVigil.Shared.csproj", "NetVigil.Shared/"]

RUN dotnet restore "NetVigil.Client/NetVigil.Client.csproj"

COPY . .
WORKDIR "/src/NetVigil.Client"
RUN dotnet build "NetVigil.Client.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NetVigil.Client.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html
RUN rm -rf ./*
COPY --from=publish /app/publish/wwwroot .
COPY NetVigil.Client/nginx.conf /etc/nginx/nginx.conf
EXPOSE 80