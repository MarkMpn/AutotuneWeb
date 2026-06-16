FROM mcr.microsoft.com/dotnet/core/sdk:3.1 AS build
WORKDIR /build
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "AutotuneWeb.dll"]
EXPOSE 80
LABEL org.opencontainers.image.created="" \
      org.opencontainers.image.title="AutotuneWeb" \
      org.opencontainers.image.description="A container based distribution of AutotuneWeb." \
      org.opencontainers.image.authors="The AutotuneWeb contributors." \
      org.opencontainers.image.vendor="The AutotuneWeb contributors." \
      org.opencontainers.image.url="https://github.com/MarkMpn/AutotuneWeb" \
      org.opencontainers.image.documentation="https://github.com/MarkMpn/AutotuneWeb/blob/dev/README.md" \
      org.opencontainers.image.source="https://github.com/MarkMpn/AutotuneWeb" \
      org.opencontainers.image.revision="" \
      org.opencontainers.image.version=""
