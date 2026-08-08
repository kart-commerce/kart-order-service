# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY KartOrderService.sln Directory.Build.props nuget.config ./
COPY packages/ packages/
COPY src/Api/KartOrderService.Api.csproj src/Api/
COPY src/Application/KartOrderService.Application.csproj src/Application/
COPY src/Domain/KartOrderService.Domain.csproj src/Domain/
COPY src/Infrastructure/KartOrderService.Infrastructure.csproj src/Infrastructure/
# The cache mount persists extracted NuGet packages under a stable id shared by every other
# kart-*-service Dockerfile, so restore stays fast (no re-download) even on a cache-miss here
# (e.g. after a .csproj change) as long as some other service's build already warmed it.
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-packages \
    dotnet restore src/Api/KartOrderService.Api.csproj

COPY src/ src/
COPY contracts/ contracts/
# Deliberately not --no-restore: some transitive packages resolve differently between a bare
# `restore` and `publish`'s own RID-aware graph (kart-payment-service/Dockerfile precedent). It
# performs its own restore, so it gets the same cache mount as the restore step above.
RUN --mount=type=cache,target=/root/.nuget/packages,id=nuget-packages \
    dotnet publish src/Api/KartOrderService.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KartOrderService.Api.dll"]
