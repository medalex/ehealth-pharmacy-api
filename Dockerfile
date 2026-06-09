FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj EHealth.Pharmacy.Api/
RUN dotnet restore EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj

COPY src/EHealth.Pharmacy.Api/ EHealth.Pharmacy.Api/
RUN dotnet publish EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj \
    -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
EXPOSE 3004
ENV ASPNETCORE_URLS=http://+:3004
ENTRYPOINT ["dotnet", "EHealth.Pharmacy.Api.dll"]
