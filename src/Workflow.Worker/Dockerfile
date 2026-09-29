FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY contracts/ contracts/
COPY src/Course.Core/Course.Core.csproj src/Course.Core/
COPY src/Course.Infrastructure/Course.Infrastructure.csproj src/Course.Infrastructure/
COPY src/Workflow.Core/Workflow.Core.csproj src/Workflow.Core/
COPY src/Course.Observability/Course.Observability.csproj src/Course.Observability/
COPY src/Workflow.Worker/Workflow.Worker.csproj src/Workflow.Worker/
RUN dotnet restore src/Workflow.Worker/Workflow.Worker.csproj

COPY src/Course.Core/ src/Course.Core/
COPY src/Course.Infrastructure/ src/Course.Infrastructure/
COPY src/Workflow.Core/ src/Workflow.Core/
COPY src/Course.Observability/ src/Course.Observability/
COPY src/Workflow.Worker/ src/Workflow.Worker/
RUN dotnet publish src/Workflow.Worker/Workflow.Worker.csproj -c Release -o /app/publish --no-restore

# ASP.NET runtime, not the plain one: since week 4 the worker serves
# /health/live, /health/ready and /metrics on its internal port.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Internal only -- no host port is published for this service.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Workflow.Worker.dll"]
