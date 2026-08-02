FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY KartOrderService.sln Directory.Build.props nuget.config ./
COPY packages/ packages/
COPY src/Api/KartOrderService.Api.csproj src/Api/
COPY src/Application/KartOrderService.Application.csproj src/Application/
COPY src/Domain/KartOrderService.Domain.csproj src/Domain/
COPY src/Infrastructure/KartOrderService.Infrastructure.csproj src/Infrastructure/
RUN dotnet restore src/Api/KartOrderService.Api.csproj

COPY src/ src/
COPY contracts/ contracts/
# Deliberately not --no-restore: some transitive packages resolve differently between a bare
# `restore` and `publish`'s own RID-aware graph (kart-payment-service/Dockerfile precedent).
RUN dotnet publish src/Api/KartOrderService.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "KartOrderService.Api.dll"]
