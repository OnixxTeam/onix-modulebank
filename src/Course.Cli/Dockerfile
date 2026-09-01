FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY contracts/ contracts/
COPY src/Course.Core/Course.Core.csproj src/Course.Core/
COPY src/Course.Infrastructure/Course.Infrastructure.csproj src/Course.Infrastructure/
COPY src/Workflow.Core/Workflow.Core.csproj src/Workflow.Core/
COPY src/Course.Cli/Course.Cli.csproj src/Course.Cli/
RUN dotnet restore src/Course.Cli/Course.Cli.csproj

COPY src/Course.Core/ src/Course.Core/
COPY src/Course.Infrastructure/ src/Course.Infrastructure/
COPY src/Workflow.Core/ src/Workflow.Core/
COPY src/Course.Cli/ src/Course.Cli/
RUN dotnet publish src/Course.Cli/Course.Cli.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
COPY db/migrations ./migrations
COPY db/manifests ./manifests
COPY db/maps ./maps

ENTRYPOINT ["dotnet", "Course.Cli.dll"]
