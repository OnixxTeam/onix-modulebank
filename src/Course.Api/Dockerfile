FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY contracts/ contracts/
COPY src/Course.Core/Course.Core.csproj src/Course.Core/
COPY src/Course.Infrastructure/Course.Infrastructure.csproj src/Course.Infrastructure/
COPY src/Workflow.Core/Workflow.Core.csproj src/Workflow.Core/
COPY src/Course.Observability/Course.Observability.csproj src/Course.Observability/
COPY src/Course.Api/Course.Api.csproj src/Course.Api/
RUN dotnet restore src/Course.Api/Course.Api.csproj

COPY src/Course.Core/ src/Course.Core/
COPY src/Course.Infrastructure/ src/Course.Infrastructure/
COPY src/Workflow.Core/ src/Workflow.Core/
COPY src/Course.Observability/ src/Course.Observability/
COPY src/Course.Api/ src/Course.Api/
RUN dotnet publish src/Course.Api/Course.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Course.Api.dll"]
