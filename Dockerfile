FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY SplunkOpsRca.slnx ./
COPY src ./src
COPY tests ./tests
RUN dotnet restore
RUN dotnet publish src/SplunkOpsRca.Api/SplunkOpsRca.Api.csproj -c Release -o /app/api /p:UseAppHost=false
RUN dotnet publish src/SplunkOpsRca.Web/SplunkOpsRca.Web.csproj -c Release -o /app/web /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SplunkOpsRca.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build /app/web .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SplunkOpsRca.Web.dll"]
