FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build-env
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY src/AutoBills/*.csproj ./
RUN dotnet restore

# Copy everything else and build
COPY src/AutoBills/ ./
ARG BUILD_NUMBER
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:5.0
WORKDIR /app
COPY --from=build-env /app/out .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "AutoBills.dll"]