FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Course.Gateway/Course.Gateway.csproj src/Course.Gateway/
RUN dotnet restore src/Course.Gateway/Course.Gateway.csproj

COPY src/Course.Gateway/ src/Course.Gateway/
RUN dotnet publish src/Course.Gateway/Course.Gateway.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Course.Gateway.dll"]
