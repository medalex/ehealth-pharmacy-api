FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj EHealth.Pharmacy.Api/
RUN dotnet restore EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj -a $TARGETARCH

COPY src/EHealth.Pharmacy.Api/ EHealth.Pharmacy.Api/
RUN dotnet publish EHealth.Pharmacy.Api/EHealth.Pharmacy.Api.csproj \
    -c Release -o /out --no-restore -a $TARGETARCH

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
EXPOSE 3004
ENV ASPNETCORE_URLS=http://+:3004
ENTRYPOINT ["dotnet", "EHealth.Pharmacy.Api.dll"]
