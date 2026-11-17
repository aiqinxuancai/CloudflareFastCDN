FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

COPY ["CloudflareFastCDN.csproj", "./"]
RUN dotnet restore "CloudflareFastCDN.csproj"

COPY . .
RUN dotnet publish "CloudflareFastCDN.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .
COPY docker-entrypoint.sh .

RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && chmod +x /app/docker-entrypoint.sh \
    && mkdir -p /data \
    && rm -rf /var/lib/apt/lists/*

# 运行设置保存在 /data/settings.json，可在 Web 管理页面中修改；
# 旧版的运行设置环境变量只会在首次启动时迁移一次，之后不再读取
ARG TZ="Etc/UTC"
ENV TZ=${TZ}

EXPOSE 19401

ENTRYPOINT ["./docker-entrypoint.sh"]

VOLUME ["/data"]
